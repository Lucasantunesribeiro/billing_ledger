using BillingLedger.BuildingBlocks.Messaging;

namespace BillingLedger.Billing.Api.Infrastructure.Messaging;

/// <summary>
/// No-op IEventBus used until MassTransit/SNS is configured (Milestone 3).
/// </summary>
internal sealed class NullEventBus : IEventBus
{
    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        => Task.CompletedTask;
}
