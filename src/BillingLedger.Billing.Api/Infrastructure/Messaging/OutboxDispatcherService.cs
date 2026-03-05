using System.Text.Json;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BillingLedger.Billing.Api.Infrastructure.Messaging;

public sealed class OutboxDispatcherService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in outbox dispatcher loop");
            }

            await Task.Delay(PollingInterval, ct).ConfigureAwait(false);
        }
    }

    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();

        // SKIP LOCKED ensures concurrent instances don't double-process the same rows
        var messages = await db.OutboxMessages
            .FromSqlRaw($"""
                SELECT * FROM infra.outbox_messages
                WHERE published_at IS NULL
                ORDER BY occurred_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (messages.Count == 0) return;

        foreach (var msg in messages)
        {
            try
            {
                var type = Type.GetType(msg.Type)
                    ?? AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a =>
                        {
                            try { return a.GetTypes(); }
                            catch { return []; }
                        })
                        .FirstOrDefault(t => t.AssemblyQualifiedName == msg.Type);

                if (type is null)
                {
                    logger.LogWarning("Unknown outbox message type: {Type}", msg.Type);
                    msg.Attempts++;
                    msg.LastError = $"Unknown type: {msg.Type}";
                    continue;
                }

                var payload = JsonSerializer.Deserialize(msg.Payload, type);
                if (payload is null)
                {
                    msg.Attempts++;
                    msg.LastError = "Deserialization returned null";
                    continue;
                }

                // Dynamically invoke IEventBus.PublishAsync<T>(payload, ct)
                var publishMethod = typeof(IEventBus)
                    .GetMethod(nameof(IEventBus.PublishAsync))!
                    .MakeGenericMethod(type);

                await (Task)publishMethod.Invoke(eventBus, [payload, ct])!;

                msg.PublishedAt = DateTime.UtcNow;
                msg.Attempts++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch outbox message {Id} of type {Type}", msg.Id, msg.Type);
                msg.Attempts++;
                msg.LastError = ex.Message;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
