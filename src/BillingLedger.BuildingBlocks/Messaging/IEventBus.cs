namespace BillingLedger.BuildingBlocks.Messaging;

public interface IEventBus
{
    Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class;
}
