using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BillingLedger.Billing.Api.Infrastructure.Observability;

/// <summary>
/// Translates unhandled exceptions to RFC 7807 ProblemDetails.
/// Internal stack traces are never exposed to callers.
/// </summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken ct)
    {
        var (statusCode, title) = exception switch
        {
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Business Rule Violation"),
            ArgumentOutOfRangeException => (StatusCodes.Status400BadRequest, "Invalid Argument"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid Argument"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource Not Found"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
        };

        logger.LogError(exception, "Unhandled exception [{Title}] {Message}", title, exception.Message);

        httpContext.Response.StatusCode = statusCode;

        // V2 FIX: Never expose raw exception.Message for 500 errors — internal details may
        // contain connection strings, file paths, or other sensitive information.
        var detail = statusCode == StatusCodes.Status500InternalServerError
            ? "An internal server error occurred. Use the CorrelationId to trace this issue."
            : exception.Message;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail
            }
        });
    }
}
