using BillingLedger.Billing.Api.Domain.Aggregates;

namespace BillingLedger.Billing.Api.Domain.Repositories;

public interface IInvoiceRepository
{
    Task AddAsync(Invoice invoice, CancellationToken ct = default);
    Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Invoice>> GetAsync(InvoiceFilter filter, CancellationToken ct = default);
}
