using System.IO;
using System.Net.Http;
using BillingLedger.Billing.Api.Infrastructure.Resilience;
using BillingLedger.BuildingBlocks.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BillingLedger.Billing.UnitTests.Infrastructure;

public class ResilientEventBusTests
{
    [Fact]
    public async Task PublishAsync_ShouldRetryTransientTransportFailures_AndEventuallySucceed()
    {
        var innerBus = new StubEventBus(
            new HttpRequestException("temporary network fault"),
            new IOException("socket reset"));

        var sut = new ResilientEventBus(
            innerBus,
            Options.Create(CreateOptions()),
            NullLogger<ResilientEventBus>.Instance);

        await sut.PublishAsync(new DummyMessage(), CancellationToken.None);

        innerBus.PublishAttempts.Should().Be(3);
        innerBus.SuccessfulPublishes.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_ShouldNotRetryNonTransientFailures()
    {
        var innerBus = new StubEventBus(new InvalidOperationException("serialization bug"));

        var sut = new ResilientEventBus(
            innerBus,
            Options.Create(CreateOptions()),
            NullLogger<ResilientEventBus>.Instance);

        var act = () => sut.PublishAsync(new DummyMessage(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        innerBus.PublishAttempts.Should().Be(1);
    }

    private static EventBusResilienceOptions CreateOptions() => new()
    {
        MaxRetryAttempts = 3,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        AttemptTimeout = TimeSpan.FromSeconds(1),
        CircuitBreakerFailureRatio = 0.5,
        CircuitBreakerMinimumThroughput = 10,
        CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(30),
        CircuitBreakerBreakDuration = TimeSpan.FromSeconds(1)
    };

    private sealed record DummyMessage;

    private sealed class StubEventBus(params Exception[] failures) : IEventBus
    {
        private readonly Queue<Exception> _failures = new(failures);

        public int PublishAttempts { get; private set; }
        public int SuccessfulPublishes { get; private set; }

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            PublishAttempts++;

            if (_failures.TryDequeue(out var failure))
                throw failure;

            SuccessfulPublishes++;
            return Task.CompletedTask;
        }
    }
}
