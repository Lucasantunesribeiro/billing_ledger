using System.Net;
using System.Net.Http.Json;
using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.Billing.Api.Application.Queries;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingLedger.IntegrationTests.Billing;

public class OutboxAtomicityTests(BillingApiFactory factory)
    : IClassFixture<BillingApiFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    // ─── ISSUE ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueInvoice_ShouldReturn200WithIssuedStatus()
    {
        var created = await CreateDraftAsync("INV-OUTBOX-ISSUE-001");

        var response = await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/issue", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var issued = await response.Content.ReadFromJsonAsync<InvoiceResponse>();
        issued!.Status.Should().Be("Issued");
        issued.IssuedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task IssueInvoice_ShouldAtomicallyCreateOutboxMessageInSameTransaction()
    {
        var created = await CreateDraftAsync("INV-OUTBOX-ATOMIC-001");
        var beforeIssue = DateTime.UtcNow.AddSeconds(-1);

        var issueResponse = await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/issue", new { });
        issueResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Query the DB directly — outbox message must exist and be unpublished
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var outboxMessages = await db.OutboxMessages
            .Where(m => m.OccurredAt >= beforeIssue
                        && EF.Functions.Like(m.Payload, $"%{created.Id}%"))
            .ToListAsync();

        outboxMessages.Should().HaveCount(1, "exactly one InvoiceIssuedV1 outbox message must be written");
        outboxMessages[0].Type.Should().Contain("InvoiceIssuedV1");
        outboxMessages[0].PublishedAt.Should().BeNull("dispatcher has not run yet");
        outboxMessages[0].Attempts.Should().Be(0);
    }

    [Fact]
    public async Task IssueInvoice_WhenAlreadyIssued_ShouldReturn400AndNoExtraOutboxMessage()
    {
        var created = await CreateDraftAsync("INV-OUTBOX-DOUBLE-ISSUE-001");
        await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/issue", new { });

        var secondIssue = await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/issue", new { });

        secondIssue.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await secondIssue.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be(400);

        // No extra outbox message written for the failed attempt
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var count = await db.OutboxMessages
            .CountAsync(m => EF.Functions.Like(m.Payload, $"%{created.Id}%")
                             && m.Type.Contains("InvoiceIssuedV1"));
        count.Should().Be(1, "only the first successful issue should create an outbox message");
    }

    [Fact]
    public async Task IssueInvoice_WithNonExistentId_ShouldReturn404()
    {
        var response = await _client.PostAsJsonAsync($"/api/invoices/{Guid.NewGuid()}/issue", new { });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── CANCEL ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelInvoice_FromDraft_ShouldReturn200WithCancelledStatus()
    {
        var created = await CreateDraftAsync("INV-OUTBOX-CANCEL-001");

        var response = await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/cancel", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await response.Content.ReadFromJsonAsync<InvoiceResponse>();
        cancelled!.Status.Should().Be("Cancelled");
        cancelled.CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelInvoice_WhenAlreadyCancelled_ShouldReturn400()
    {
        var created = await CreateDraftAsync("INV-OUTBOX-CANCEL-ALREADY-001");
        await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/cancel", new { });

        var secondCancel = await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/cancel", new { });

        secondCancel.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CancelInvoice_WithNonExistentId_ShouldReturn404()
    {
        var response = await _client.PostAsJsonAsync($"/api/invoices/{Guid.NewGuid()}/cancel", new { });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── DISPATCHER ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatcher_WhenTriggeredManually_ShouldMarkOutboxMessagesAsPublished()
    {
        var created = await CreateDraftAsync("INV-DISPATCHER-001");
        await _client.PostAsJsonAsync($"/api/invoices/{created.Id}/issue", new { });

        // Manually trigger one dispatcher batch
        await using var scope = factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<BillingLedger.Billing.Api.Infrastructure.Messaging.OutboxDispatcherService>();
        await dispatcher.ProcessBatchAsync(CancellationToken.None);

        // Outbox message should now be marked as published
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var msg = await db.OutboxMessages
            .FirstOrDefaultAsync(m => EF.Functions.Like(m.Payload, $"%{created.Id}%")
                                      && m.Type.Contains("InvoiceIssuedV1"));

        msg.Should().NotBeNull();
        msg!.PublishedAt.Should().NotBeNull("dispatcher must mark messages as published");
        msg.Attempts.Should().Be(1);

        // FakeEventBus should have captured the message
        factory.EventBus.Published.Should().Contain(
            m => m.GetType().Name.Contains("InvoiceIssuedV1"),
            "the integration event must have been published via IEventBus");
    }

    // ─── HELPER ──────────────────────────────────────────────────────────────

    private async Task<InvoiceResponse> CreateDraftAsync(string externalRef)
    {
        var response = await _client.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            CustomerId: Guid.NewGuid(),
            Amount: 300m,
            Currency: "BRL",
            DueDate: DateTime.UtcNow.AddDays(30),
            ExternalReference: externalRef));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvoiceResponse>())!;
    }
}
