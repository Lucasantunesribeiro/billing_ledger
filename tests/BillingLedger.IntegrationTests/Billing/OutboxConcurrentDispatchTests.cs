using System.Collections.Concurrent;
using System.Text.Json;
using BillingLedger.Billing.Api.Infrastructure.Messaging;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.BuildingBlocks.Messaging;
using BillingLedger.BuildingBlocks.Outbox;
using BillingLedger.Contracts.Billing;
using BillingLedger.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BillingLedger.IntegrationTests.Billing;

/// <summary>
/// Duas instancias do dispatcher rodando ao mesmo tempo nao podem publicar a mesma linha do Outbox.
/// O FOR UPDATE SKIP LOCKED so protege enquanto a transacao que fez o SELECT estiver aberta.
/// </summary>
public sealed class OutboxConcurrentDispatchTests(OutboxConcurrentDispatchTests.SlowBusFactory factory)
    : IClassFixture<OutboxConcurrentDispatchTests.SlowBusFactory>
{
    public sealed class SlowRecordingEventBus : IEventBus
    {
        public ConcurrentBag<object> Published { get; } = [];

        public async Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            // Simula a latencia do broker: alarga a janela entre o SELECT e o UPDATE published_at.
            await Task.Delay(30, ct);
            Published.Add(message);
        }
    }

    public sealed class SlowBusFactory : BillingApiFactory
    {
        public SlowRecordingEventBus SlowBus { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEventBus>();
                services.AddSingleton<IEventBus>(SlowBus);
            });
        }
    }

    [Fact]
    public async Task TwoDispatchersInParallel_ShouldPublishEachOutboxMessageExactlyOnce()
    {
        const int messageCount = 10;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            for (var i = 0; i < messageCount; i++)
            {
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Type = typeof(InvoiceOverdueV1).AssemblyQualifiedName!,
                    Payload = JsonSerializer.Serialize(new InvoiceOverdueV1
                    {
                        InvoiceId = Guid.NewGuid(),
                        OverdueAt = DateTime.UtcNow
                    }),
                    OccurredAt = DateTime.UtcNow.AddMilliseconds(i)
                });
            }
            await db.SaveChangesAsync();
        }

        // Duas instancias independentes (como dois pods do Billing.Api).
        var first = ActivatorUtilities.CreateInstance<OutboxDispatcherService>(factory.Services);
        var second = ActivatorUtilities.CreateInstance<OutboxDispatcherService>(factory.Services);

        await Task.WhenAll(
            first.ProcessBatchAsync(CancellationToken.None),
            second.ProcessBatchAsync(CancellationToken.None));
        // Uma rodada extra publica o que tiver sido pulado (SKIP LOCKED) e nada mais.
        await first.ProcessBatchAsync(CancellationToken.None);

        var eventIds = factory.SlowBus.Published.Cast<InvoiceOverdueV1>().Select(m => m.EventId).ToList();
        eventIds.Should().HaveCount(messageCount, "cada linha do outbox deve ser publicada uma unica vez");
        eventIds.Should().OnlyHaveUniqueItems();

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<BillingDbContext>();
        (await assertDb.OutboxMessages.CountAsync(m => m.PublishedAt == null)).Should().Be(0);
        (await assertDb.OutboxMessages.AllAsync(m => m.Attempts == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task RedispatchedPayload_ShouldKeepTheSameEventId()
    {
        // A chave de idempotencia do ledger e o EventId gravado no payload do Outbox:
        // desserializar o mesmo payload duas vezes (republicacao apos falha) nao gera EventId novo.
        var payload = JsonSerializer.Serialize(new InvoicePaidV1
        {
            InvoiceId = Guid.NewGuid(),
            Amount = 150.35m,
            Currency = "BRL",
            PaidAt = DateTime.UtcNow
        });

        var a = JsonSerializer.Deserialize<InvoicePaidV1>(payload)!;
        var b = JsonSerializer.Deserialize<InvoicePaidV1>(payload)!;

        a.EventId.Should().NotBeEmpty();
        b.EventId.Should().Be(a.EventId);
        b.Amount.Should().Be(150.35m);
    }
}
