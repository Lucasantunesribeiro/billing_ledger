using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace BillingLedger.BuildingBlocks.Observability;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        // V5 FIX: Validate that the incoming header is a well-formed GUID.
        // Arbitrary strings accepted as-is are a log-injection vector (newlines, control chars).
        var rawHeader = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = Guid.TryParse(rawHeader, out var parsed)
            ? parsed.ToString()
            : Guid.NewGuid().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
