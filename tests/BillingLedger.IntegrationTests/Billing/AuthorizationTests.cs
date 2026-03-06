using System.Net;
using System.Net.Http.Json;
using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.Billing.Api.Application.Queries;
using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingLedger.IntegrationTests.Billing;

/// <summary>
/// Proves JWT + RBAC security and atomic AuditLog persistence.
///
/// 401 → no token; 403 → authenticated but wrong role; 200 + AuditLog → happy path.
/// </summary>
public sealed class AuthorizationTests(BillingApiFactory factory)
    : IClassFixture<BillingApiFactory>
{
    // ─── 401 Unauthorized ────────────────────────────────────────────────────

    [Fact]
    public async Task GetInvoices_WithoutToken_ShouldReturn401()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/invoices");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateInvoice_WithoutToken_ShouldReturn401()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            Guid.NewGuid(), 100m, "BRL", DateTime.UtcNow.AddDays(10), null));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── 403 Forbidden ───────────────────────────────────────────────────────

    [Fact]
    public async Task IssueInvoice_WithReadOnlyRole_ShouldReturn403()
    {
        // Create an invoice using a Finance user
        var financeClient = factory.CreateAuthenticatedClient(role: "Finance");
        var createResp = await financeClient.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            Guid.NewGuid(), 200m, "BRL", DateTime.UtcNow.AddDays(30), null));
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<InvoiceResponse>();

        // ReadOnly user tries to issue → must be rejected
        var readOnlyClient = factory.CreateAuthenticatedClient(role: "ReadOnly");
        var issueResp = await readOnlyClient.PostAsync($"/api/invoices/{created!.Id}/issue", null);

        issueResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CancelInvoice_WithReadOnlyRole_ShouldReturn403()
    {
        var financeClient = factory.CreateAuthenticatedClient(role: "Finance");
        var createResp = await financeClient.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            Guid.NewGuid(), 200m, "BRL", DateTime.UtcNow.AddDays(30), null));
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<InvoiceResponse>();

        var readOnlyClient = factory.CreateAuthenticatedClient(role: "ReadOnly");
        var cancelResp = await readOnlyClient.PostAsync($"/api/invoices/{created!.Id}/cancel", null);

        cancelResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── 200 + AuditLog (Finance role) ───────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_WithFinanceRole_ShouldPersistAuditLog()
    {
        var actorUserId = Guid.NewGuid().ToString();
        var client = factory.CreateAuthenticatedClient(userId: actorUserId, role: "Finance");

        var response = await client.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            Guid.NewGuid(), 500m, "BRL", DateTime.UtcNow.AddDays(30), null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<InvoiceResponse>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var log = await db.AuditLogs
            .FirstOrDefaultAsync(a =>
                a.ResourceId == created!.Id.ToString() &&
                a.Action == "InvoiceCreated");

        log.Should().NotBeNull("an AuditLog must be written atomically with the invoice");
        log!.ActorUserId.Should().Be(actorUserId);
        log.ResourceType.Should().Be("Invoice");
    }

    [Fact]
    public async Task IssueInvoice_WithAdminRole_ShouldPersistAuditLog()
    {
        var actorUserId = Guid.NewGuid().ToString();
        var client = factory.CreateAuthenticatedClient(userId: actorUserId, role: "Admin");

        // Create
        var createResp = await client.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            Guid.NewGuid(), 300m, "BRL", DateTime.UtcNow.AddDays(30), null));
        var created = await createResp.Content.ReadFromJsonAsync<InvoiceResponse>();

        // Issue
        var issueResp = await client.PostAsync($"/api/invoices/{created!.Id}/issue", null);
        issueResp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var log = await db.AuditLogs
            .FirstOrDefaultAsync(a =>
                a.ResourceId == created.Id.ToString() &&
                a.Action == "InvoiceIssued");

        log.Should().NotBeNull();
        log!.ActorUserId.Should().Be(actorUserId);
    }
}
