using BillingLedger.Billing.Api.Application.Commands;
using BillingLedger.Billing.Api.Application.Queries;
using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.SharedKernel.Primitives;
using Microsoft.AspNetCore.Mvc;

namespace BillingLedger.Billing.Api.Controllers;

[ApiController]
[Route("api/invoices")]
[Produces("application/json")]
public class InvoicesController(
    IInvoiceRepository repository,
    IUnitOfWork unitOfWork) : ControllerBase
{
    /// <summary>Creates a new invoice in Draft status.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateInvoiceRequest request,
        CancellationToken ct)
    {
        if (request.Amount < 0)
            return Problem(title: "Invalid Amount", detail: "Amount must be non-negative.", statusCode: 400);

        if (request.DueDate <= DateTime.UtcNow)
            return Problem(title: "Invalid Due Date", detail: "DueDate must be in the future.", statusCode: 400);

        var money = Money.Of(request.Amount, request.Currency);

        var externalRef = string.IsNullOrWhiteSpace(request.ExternalReference)
            ? $"INV-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}"
            : request.ExternalReference;

        var correlationId = HttpContext.Items["X-Correlation-Id"] is string cid && Guid.TryParse(cid, out var parsed)
            ? parsed
            : Guid.NewGuid();

        var invoice = Invoice.Create(request.CustomerId, money, request.DueDate, externalRef, correlationId);

        await repository.AddAsync(invoice, ct);
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
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Issue(Guid id, CancellationToken ct)
    {
        var invoice = await repository.GetByIdAsync(id, ct);
        if (invoice is null)
            return Problem(title: "Not Found", detail: $"Invoice '{id}' not found.", statusCode: 404);

        var correlationId = HttpContext.Items["X-Correlation-Id"] is string cid && Guid.TryParse(cid, out var parsed)
            ? parsed
            : Guid.NewGuid();

        invoice.Issue(correlationId);
        await unitOfWork.SaveChangesAsync(ct);

        return Ok(InvoiceResponse.From(invoice));
    }

    /// <summary>Cancels an invoice (Draft|Issued|Overdue → Cancelled).</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var invoice = await repository.GetByIdAsync(id, ct);
        if (invoice is null)
            return Problem(title: "Not Found", detail: $"Invoice '{id}' not found.", statusCode: 404);

        var correlationId = HttpContext.Items["X-Correlation-Id"] is string cid && Guid.TryParse(cid, out var parsed)
            ? parsed
            : Guid.NewGuid();

        invoice.Cancel(correlationId);
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
