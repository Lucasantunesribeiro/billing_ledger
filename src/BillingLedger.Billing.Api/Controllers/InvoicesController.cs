using System.Security.Claims;
using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.Billing.Api.Application.Queries;
using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.Billing.Api.Infrastructure.Audit;
using BillingLedger.SharedKernel.Primitives;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingLedger.Billing.Api.Controllers;

[ApiController]
[Route("api/invoices")]
[Produces("application/json")]
[Authorize]
public class InvoicesController(
    IInvoiceRepository repository,
    IUnitOfWork unitOfWork,
    IAuditService auditService) : ControllerBase
{
    private string ActorUserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";

    private Guid CorrelationId =>
        HttpContext.Items["X-Correlation-Id"] is string cid && Guid.TryParse(cid, out var parsed)
            ? parsed
            : Guid.NewGuid();

    /// <summary>Creates a new invoice in Draft status.</summary>
    [HttpPost]
    [Authorize(Policy = "Finance")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateInvoiceRequest request,
        CancellationToken ct)
    {
        // Input validation is handled by FluentValidation (AddFluentValidationAutoValidation).
        // ModelState is invalid → 400 with ValidationProblemDetails automatically returned.
        var money = Money.Of(request.Amount, request.Currency);

        var externalRef = string.IsNullOrWhiteSpace(request.ExternalReference)
            ? $"INV-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}"
            : request.ExternalReference;

        var invoice = Invoice.Create(request.CustomerId, money, request.DueDate, externalRef, CorrelationId);

        await repository.AddAsync(invoice, ct);
        auditService.Record(ActorUserId, "InvoiceCreated", "Invoice", invoice.Id.Value.ToString());
        await unitOfWork.SaveChangesAsync(ct);

        var response = InvoiceResponse.From(invoice);
        return CreatedAtAction(nameof(GetById), new { id = invoice.Id.Value }, response);
    }

    /// <summary>Gets an invoice by its identifier.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var invoice = await repository.GetByIdAsync(id, ct);
        if (invoice is null)
            return Problem(title: "Not Found", detail: $"Invoice '{id}' not found.", statusCode: 404);

        return Ok(InvoiceResponse.From(invoice));
    }

    /// <summary>Issues an invoice (Draft → Issued).</summary>
    [HttpPost("{id:guid}/issue")]
    [Authorize(Policy = "Finance")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Issue(Guid id, CancellationToken ct)
    {
        var invoice = await repository.GetByIdAsync(id, ct);
        if (invoice is null)
            return Problem(title: "Not Found", detail: $"Invoice '{id}' not found.", statusCode: 404);

        invoice.Issue(CorrelationId);
        auditService.Record(ActorUserId, "InvoiceIssued", "Invoice", id.ToString());
        await unitOfWork.SaveChangesAsync(ct);

        return Ok(InvoiceResponse.From(invoice));
    }

    /// <summary>Cancels an invoice (Draft|Issued|Overdue → Cancelled).</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Finance")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var invoice = await repository.GetByIdAsync(id, ct);
        if (invoice is null)
            return Problem(title: "Not Found", detail: $"Invoice '{id}' not found.", statusCode: 404);

        invoice.Cancel(CorrelationId);
        auditService.Record(ActorUserId, "InvoiceCancelled", "Invoice", id.ToString());
        await unitOfWork.SaveChangesAsync(ct);

        return Ok(InvoiceResponse.From(invoice));
    }

    /// <summary>Lists invoices with optional filters.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<InvoiceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] InvoiceStatus? status,
        [FromQuery] Guid? customerId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var filter = new InvoiceFilter
        {
            Status = status,
            CustomerId = customerId,
            From = from,
            To = to
        };

        var invoices = await repository.GetAsync(filter, ct);
        return Ok(invoices.Select(InvoiceResponse.From));
    }
}
