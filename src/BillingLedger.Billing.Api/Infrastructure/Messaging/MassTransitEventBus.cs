using BillingLedger.BuildingBlocks.Messaging;
using MassTransit;

namespace BillingLedger.Billing.Api.Infrastructure.Messaging;

/// <summary>
/// IEventBus implementation that delegates to MassTransit's IPublishEndpoint.
/// Used in production (in-memory for Milestones 1-2, SNS/SQS in Milestone 3).
/// </summary>
public sealed class MassTransitEventBus(IPublishEndpoint publishEndpoint) : IEventBus
{
    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        => publishEndpoint.Publish(message, ct);
}
