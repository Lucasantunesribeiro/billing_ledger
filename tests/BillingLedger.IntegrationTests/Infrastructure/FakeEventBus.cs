using BillingLedger.BuildingBlocks.Messaging;

namespace BillingLedger.IntegrationTests.Infrastructure;

public sealed class FakeEventBus : IEventBus
{
    private readonly List<object> _published = [];
    public IReadOnlyList<object> Published => _published.AsReadOnly();

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        _published.Add(message);
        return Task.CompletedTask;
    }
}
