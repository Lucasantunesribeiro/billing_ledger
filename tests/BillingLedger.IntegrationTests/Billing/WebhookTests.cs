using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingLedger.Contracts.Payments;
using BillingLedger.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace BillingLedger.IntegrationTests.Billing;

public class WebhookTests(BillingApiFactory factory) : IClassFixture<BillingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private const string WebhookSecret = "test-secret";

    [Fact]
    public async Task Webhook_WithValidSignature_ShouldReturn200AndPublishPaymentReceivedV1()
    {
        var invoiceId = Guid.NewGuid();
        var payload = new
        {
            InvoiceId = invoiceId,
            ExternalPaymentId = "pix-webhook-001",
            Provider = "PIX",
            Amount = 150.00m
        };
        var json = JsonSerializer.Serialize(payload);
        var signature = ComputeHmac(WebhookSecret, json);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Webhook-Signature", signature);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.EventBus.Published
            .OfType<PaymentReceivedV1>()
            .Should().Contain(e =>
                e.InvoiceId == invoiceId &&
                e.ExternalPaymentId == "pix-webhook-001" &&
                e.Provider == "PIX",
                "webhook must publish PaymentReceivedV1 directly via IEventBus");
    }

    [Fact]
    public async Task Webhook_WithInvalidSignature_ShouldReturn403()
    {
        var payload = new { InvoiceId = Guid.NewGuid(), ExternalPaymentId = "pix-bad-sig", Provider = "PIX", Amount = 50m };
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Webhook-Signature", "sha256=0000deadbeef");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Webhook_WithMissingSignature_ShouldReturn403()
    {
        var payload = new { InvoiceId = Guid.NewGuid(), ExternalPaymentId = "pix-no-sig", Provider = "PIX", Amount = 50m };
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        // No X-Webhook-Signature header

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static string ComputeHmac(string secret, string body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(body);
        using var hmac = new HMACSHA256(key);
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }
}
