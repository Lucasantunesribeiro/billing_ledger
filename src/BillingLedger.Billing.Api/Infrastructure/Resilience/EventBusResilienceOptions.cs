namespace BillingLedger.Billing.Api.Infrastructure.Resilience;

public sealed class EventBusResilienceOptions
{
    public const string SectionName = "Resilience:EventBus";

    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerMinimumThroughput { get; set; } = 5;
    public TimeSpan CircuitBreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}
