using System.Net;
using System.Net.Http.Json;
using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.Billing.Api.Application.Queries;
using BillingLedger.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace BillingLedger.IntegrationTests.Billing;

public class InvoicesControllerTests(BillingApiFactory factory)
    : IClassFixture<BillingApiFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    // ─── POST /api/invoices ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_WithValidRequest_ShouldReturn201WithDraftInvoice()
    {
        var request = new CreateInvoiceRequest(
            CustomerId: Guid.NewGuid(),
            Amount: 350.00m,
            Currency: "BRL",
            DueDate: DateTime.UtcNow.AddDays(30),
            ExternalReference: null);

        var response = await _client.PostAsJsonAsync("/api/invoices", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var invoice = await response.Content.ReadFromJsonAsync<InvoiceResponse>();
        invoice.Should().NotBeNull();
        invoice!.Status.Should().Be("Draft");
        invoice.Amount.Should().Be(350.00m);
        invoice.Currency.Should().Be("BRL");
        invoice.CustomerId.Should().Be(request.CustomerId);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateInvoice_WithNegativeAmount_ShouldReturn400ProblemDetails()
    {
        var request = new CreateInvoiceRequest(
            CustomerId: Guid.NewGuid(),
            Amount: -1m,
            Currency: "BRL",
            DueDate: DateTime.UtcNow.AddDays(30),
            ExternalReference: null);

        var response = await _client.PostAsJsonAsync("/api/invoices", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(400);
    }

    [Fact]
    public async Task CreateInvoice_WithPastDueDate_ShouldReturn400ProblemDetails()
    {
        var request = new CreateInvoiceRequest(
            CustomerId: Guid.NewGuid(),
            Amount: 100m,
            Currency: "BRL",
            DueDate: DateTime.UtcNow.AddDays(-1),
            ExternalReference: null);

        var response = await _client.PostAsJsonAsync("/api/invoices", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── GET /api/invoices/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task GetById_WithExistingId_ShouldReturn200()
    {
        // Create first
        var createRequest = new CreateInvoiceRequest(
            CustomerId: Guid.NewGuid(), Amount: 100m, Currency: "BRL",
            DueDate: DateTime.UtcNow.AddDays(15), ExternalReference: "INV-GET-001");
        var createResponse = await _client.PostAsJsonAsync("/api/invoices", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<InvoiceResponse>();

        // Get by id
        var response = await _client.GetAsync($"/api/invoices/{created!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var invoice = await response.Content.ReadFromJsonAsync<InvoiceResponse>();
        invoice!.Id.Should().Be(created.Id);
        invoice.ExternalReference.Should().Be("INV-GET-001");
    }

    [Fact]
    public async Task GetById_WithNonExistentId_ShouldReturn404ProblemDetails()
    {
        var response = await _client.GetAsync($"/api/invoices/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(404);
    }

    // ─── GET /api/invoices ───────────────────────────────────────────────────

    [Fact]
    public async Task List_WithNoFilter_ShouldReturn200WithResults()
    {
        await _client.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            Guid.NewGuid(), 100m, "BRL", DateTime.UtcNow.AddDays(10), null));

        var response = await _client.GetAsync("/api/invoices");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var invoices = await response.Content.ReadFromJsonAsync<List<InvoiceResponse>>();
        invoices.Should().NotBeNull();
        invoices!.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task List_WithStatusFilter_ShouldReturnOnlyMatchingStatus()
    {
        // Create a draft invoice
        await _client.PostAsJsonAsync("/api/invoices", new CreateInvoiceRequest(
            Guid.NewGuid(), 100m, "BRL", DateTime.UtcNow.AddDays(10), "INV-FILTER-STATUS"));

        var response = await _client.GetAsync("/api/invoices?status=Draft");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var invoices = await response.Content.ReadFromJsonAsync<List<InvoiceResponse>>();
        invoices.Should().AllSatisfy(i => i.Status.Should().Be("Draft"));
    }
}
