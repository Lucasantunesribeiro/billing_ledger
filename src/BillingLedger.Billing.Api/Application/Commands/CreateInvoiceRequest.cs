namespace BillingLedger.Billing.Api.Application.Commands;

/// <summary>Dados para criar uma fatura em rascunho (Draft).</summary>
/// <param name="CustomerId">Identificador do cliente.</param>
/// <param name="Amount">Valor em decimal, maior que zero e com no maximo 2 casas (ex.: 150.35).</param>
/// <param name="Currency">Codigo ISO 4217 em maiusculas (ex.: BRL).</param>
/// <param name="DueDate">Vencimento (UTC), obrigatoriamente no futuro.</param>
/// <param name="ExternalReference">Referencia externa opcional (ate 100 caracteres); gerada se ausente.</param>
public record CreateInvoiceRequest(
    Guid CustomerId,
    decimal Amount,
    string Currency,
    DateTime DueDate,
    string? ExternalReference);
