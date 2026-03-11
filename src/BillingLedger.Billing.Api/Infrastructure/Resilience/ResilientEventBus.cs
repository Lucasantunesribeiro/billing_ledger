using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using BillingLedger.BuildingBlocks.Messaging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace BillingLedger.Billing.Api.Infrastructure.Resilience;

public sealed class ResilientEventBus : IEventBus
{
    private readonly IEventBus _innerBus;
    private readonly ILogger<ResilientEventBus> _logger;
    private readonly ResiliencePipeline _pipeline;

    public ResilientEventBus(
        IEventBus innerBus,
        IOptions<EventBusResilienceOptions> options,
        ILogger<ResilientEventBus> logger)
    {
        _innerBus = innerBus;
        _logger = logger;
        _pipeline = BuildPipeline(options.Value);
    }

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class =>
        _pipeline.ExecuteAsync(
            static (state, token) => new ValueTask(state.innerBus.PublishAsync(state.message, token)),
            (innerBus: _innerBus, message),
            ct).AsTask();

    private ResiliencePipeline BuildPipeline(EventBusResilienceOptions options)
    {
        var shouldHandle = new PredicateBuilder()
            .Handle<HttpRequestException>()
            .Handle<IOException>()
            .Handle<SocketException>()
            .Handle<TimeoutException>()
            .Handle<TimeoutRejectedException>()
            .Handle<Exception>(IsAwsTransportException);

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = shouldHandle,
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = options.BaseDelay,
                MaxDelay = options.MaxDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "Retrying event publish after transient failure. Attempt={Attempt} DelayMs={DelayMs}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds);

                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = shouldHandle,
                FailureRatio = options.CircuitBreakerFailureRatio,
                MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                SamplingDuration = options.CircuitBreakerSamplingDuration,
                BreakDuration = options.CircuitBreakerBreakDuration,
                OnOpened = args =>
                {
                    _logger.LogError(
                        args.Outcome.Exception,
                        "Event bus circuit opened for {BreakDurationMs} ms due to repeated transport failures.",
                        args.BreakDuration.TotalMilliseconds);

                    return default;
                },
                OnClosed = _ =>
                {
                    _logger.LogInformation("Event bus circuit closed and publishing resumed.");
                    return default;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = options.AttemptTimeout
            })
            .Build();
    }

    private static bool IsAwsTransportException(Exception ex)
    {
        Exception? current = ex;

        while (current is not null)
        {
            var fullName = current.GetType().FullName;
            if (fullName is not null &&
                (fullName.Contains("Amazon.Runtime.AmazonServiceException", StringComparison.Ordinal) ||
                 fullName.Contains("Amazon.Runtime.AmazonClientException", StringComparison.Ordinal)))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }
}
