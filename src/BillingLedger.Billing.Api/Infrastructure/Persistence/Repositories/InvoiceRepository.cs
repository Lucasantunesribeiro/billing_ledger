using BillingLedger.Billing.Api.Domain.Aggregates;
using BillingLedger.Billing.Api.Domain.Repositories;
using BillingLedger.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;

namespace BillingLedger.Billing.Api.Infrastructure.Persistence.Repositories;

public sealed class InvoiceRepository(BillingDbContext dbContext) : IInvoiceRepository
{
    public async Task AddAsync(Invoice invoice, CancellationToken ct = default) =>
        await dbContext.Invoices.AddAsync(invoice, ct);

    public async Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await dbContext.Invoices
            .FirstOrDefaultAsync(i => i.Id == EntityId.From(id), ct);

    public async Task<IReadOnlyList<Invoice>> GetAsync(InvoiceFilter filter, CancellationToken ct = default)
    {
        var query = dbContext.Invoices.AsQueryable();

        if (filter.Status.HasValue)
            query = query.Where(i => i.Status == filter.Status.Value);

        if (filter.CustomerId.HasValue)
            query = query.Where(i => i.CustomerId == filter.CustomerId.Value);

        if (filter.From.HasValue)
            query = query.Where(i => i.CreatedAt >= filter.From.Value);

        if (filter.To.HasValue)
            query = query.Where(i => i.CreatedAt <= filter.To.Value);

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
    }
}
