using Microsoft.Extensions.Http.Resilience;

namespace BillingLedger.Billing.Api.Infrastructure.Resilience;

public static class ResilienceServiceCollectionExtensions
{
    public static IServiceCollection AddBillingIntegrationResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<EventBusResilienceOptions>()
            .Bind(configuration.GetSection(EventBusResilienceOptions.SectionName));

        services
            .AddHttpClient(DownstreamHttpClientNames.Integrations, client =>
            {
                // Resilience handler controls timeouts for outbound calls.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddStandardResilienceHandler()
            .Configure(configuration.GetSection("Resilience:DownstreamHttp"));

        return services;
    }
}
