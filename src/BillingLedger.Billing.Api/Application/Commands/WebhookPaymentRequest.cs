namespace BillingLedger.Billing.Api.Application.Commands;

/// <summary>Corpo do webhook enviado pelo provedor de pagamento.</summary>
/// <param name="InvoiceId">Fatura que esta sendo paga.</param>
/// <param name="ExternalPaymentId">Identificador do pagamento no provedor (chave de idempotencia junto com Provider).</param>
/// <param name="Provider">Provedor (ex.: PIX, BOLETO, CARD).</param>
/// <param name="Amount">Valor pago em decimal, maior que zero e com no maximo 2 casas.</param>
/// <param name="Currency">ISO 4217 opcional; quando ausente, assume-se a moeda da fatura.</param>
public sealed record WebhookPaymentRequest(
    Guid InvoiceId,
    string ExternalPaymentId,
    string Provider,
    decimal Amount,
    string? Currency = null);
