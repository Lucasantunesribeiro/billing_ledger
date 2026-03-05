using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.BuildingBlocks.Messaging;
using BillingLedger.Contracts.Payments;
using Microsoft.AspNetCore.Mvc;

namespace BillingLedger.Billing.Api.Controllers;

[ApiController]
[Route("api/payments")]
[Produces("application/json")]
public sealed class PaymentsController(
    IEventBus eventBus,
    IConfiguration config,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>
    /// Receives payment provider webhooks (PIX, Stripe, etc.).
    /// Validates HMAC-SHA256 signature then publishes PaymentReceivedV1 directly
    /// to the event bus — no Outbox required for inbound external events.
    /// </summary>
    [HttpPost("webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // Buffer the body so we can read it twice (raw for HMAC, deserialized for business)
        Request.EnableBuffering();

        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(ct);
            Request.Body.Position = 0;
        }

        if (!IsSignatureValid(rawBody))
            return Problem(title: "Forbidden", detail: "Invalid or missing webhook signature.", statusCode: 403);

        var payload = JsonSerializer.Deserialize<WebhookPaymentRequest>(
            rawBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (payload is null)
            return Problem(title: "Bad Request", detail: "Invalid request body.", statusCode: 400);

        var correlationId = HttpContext.Items["X-Correlation-Id"] is string cid
            && Guid.TryParse(cid, out var parsed)
                ? parsed
                : Guid.NewGuid();

        await eventBus.PublishAsync(new PaymentReceivedV1
        {
            InvoiceId = payload.InvoiceId,
            ExternalPaymentId = payload.ExternalPaymentId,
            Provider = payload.Provider,
            Amount = payload.Amount,
            ReceivedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        }, ct);

        logger.LogInformation(
            "Webhook accepted. InvoiceId={InvoiceId} Provider={Provider} ExternalPaymentId={ExternalPaymentId}",
            payload.InvoiceId, payload.Provider, payload.ExternalPaymentId);

        return Ok();
    }

    private bool IsSignatureValid(string rawBody)
    {
        var secret = config["Payments:WebhookSecret"];
        if (string.IsNullOrEmpty(secret))
            return false; // No secret configured → always reject

        var signatureHeader = Request.Headers["X-Webhook-Signature"].ToString();
        if (string.IsNullOrEmpty(signatureHeader))
            return false;

        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(rawBody);
        using var hmac = new HMACSHA256(key);
        var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();

        // Constant-time comparison prevents timing attacks
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader));
    }
}
