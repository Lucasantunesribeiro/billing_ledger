# BillingLedger

Backend enterprise-grade event-driven para emissão de cobranças, processamento de pagamentos e conciliação via ledger, construído em **.NET 9 / C#** com DDD, Outbox Pattern, idempotência e mensageria real na AWS.

---

## Visão Geral da Arquitetura

```mermaid
graph TD
    Client -->|HTTPS| API[Billing.Api]
    API -->|Outbox tx| PG[(PostgreSQL)]
    API -->|Dispatch| SNS[SNS: billing-invoice-events]

    SNS -->|fanout| SQS_BW[SQS: billing-worker-queue]
    SNS -->|fanout| SQS_LW[SQS: ledger-worker-queue]

    EXT[Payment Provider Webhook] -->|POST /payments/webhook| API
    API -->|Publish| SNS_PAY[SNS: payments-payment-events]
    SNS_PAY -->|fanout| SQS_PW[SQS: payments-worker-queue]
    SNS_PAY -->|fanout| SQS_BW

    PW[Payments.Worker] -->|Consumes| SQS_PW
    PW -->|PaymentConfirmed| SNS_PAY

    LW[Ledger.Worker] -->|Consumes| SQS_LW
    LW -->|LedgerEntry| PG

    SQS_PW -->|fail x3| DLQ_PW[DLQ: payments]
    SQS_LW -->|fail x3| DLQ_LW[DLQ: ledger]
    SQS_BW -->|fail x3| DLQ_BW[DLQ: billing]
```

## SAGA de Pagamento

```
InvoiceIssued → PaymentReceived → PaymentConfirmed → InvoicePaid → LedgerEntryCreated
```

Transição `Overdue` é disparada por job agendado no `Billing.Api`.

## Catálogo de Eventos (V1)

| Evento                 | Publicado por         | Consumido por               | Tópico SNS              |
| ---------------------- | --------------------- | --------------------------- | ----------------------- |
| `InvoiceIssuedV1`      | Billing.Api (Outbox)  | Ledger.Worker               | billing-invoice-events  |
| `PaymentReceivedV1`    | Billing.Api (webhook) | Payments.Worker             | payments-payment-events |
| `PaymentConfirmedV1`   | Payments.Worker       | Billing.Api, Billing.Worker | payments-payment-events |
| `InvoicePaidV1`        | Billing.Api (Outbox)  | Ledger.Worker               | billing-invoice-events  |
| `InvoiceOverdueV1`     | Billing.Api (job)     | Ledger.Worker               | billing-invoice-events  |
| `LedgerEntryCreatedV1` | Ledger.Worker         | —                           | —                       |

Todos os eventos incluem `EventId`, `CorrelationId` e `SchemaVersion = 1`.

## Schemas do Banco (PostgreSQL)

| Schema     | Responsável     | Conteúdo                        |
| ---------- | --------------- | ------------------------------- |
| `billing`  | Billing.Api     | `invoices`                      |
| `payments` | Payments.Worker | `payment_attempts`              |
| `ledger`   | Ledger.Worker   | `ledger_entries`                |
| `infra`    | Todos           | `outbox_messages`, `audit_logs` |

## Como Rodar Localmente

### Pré-requisitos
- Docker e Docker Compose
- .NET 9 SDK
- `awslocal` (pip install awscli-local) — para interagir com LocalStack

### 1. Subir a infraestrutura

```bash
docker compose up -d
```

Aguarde todos os healthchecks ficarem `healthy` (especialmente LocalStack ~30s).

### 2. Aplicar migrações EF Core

```bash
# Billing context
dotnet ef database update --project src/BillingLedger.Billing.Api

# Payments context
dotnet ef database update --project src/BillingLedger.Payments.Worker

# Ledger context
dotnet ef database update --project src/BillingLedger.Ledger.Worker
```

### 3. Rodar os serviços

```bash
# Terminal 1 — API
dotnet run --project src/BillingLedger.Billing.Api

# Terminal 2 — Payments Worker
dotnet run --project src/BillingLedger.Payments.Worker

# Terminal 3 — Ledger Worker
dotnet run --project src/BillingLedger.Ledger.Worker
```

Swagger disponível em: `http://localhost:5082/swagger`

### 4. Exemplo de fluxo completo (curl)

```bash
# 1. Criar invoice (draft)
curl -X POST http://localhost:5082/api/invoices \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"customerId":"00000000-0000-0000-0000-000000000001","amount":150.00,"currency":"BRL","dueDate":"2026-04-01"}'

# 2. Emitir invoice
curl -X POST http://localhost:5082/api/invoices/{id}/issue \
  -H "Authorization: Bearer <token>"

# 3. Simular pagamento recebido (webhook — autenticado via HMAC-SHA256)
# Gere a assinatura: echo -n '<body>' | openssl dgst -sha256 -hmac '<secret>'
BODY='{"invoiceId":"{id}","externalPaymentId":"pix-abc123","provider":"PIX","amount":150.00}'
SIG="sha256=$(echo -n "$BODY" | openssl dgst -sha256 -hmac 'dev-webhook-secret-32-chars!!' | awk '{print $2}')"
curl -X POST http://localhost:5082/api/payments/webhook \
  -H "X-Webhook-Signature: $SIG" \
  -H "Content-Type: application/json" \
  -d "$BODY"
```

## Testes

```bash
# Todos os testes
dotnet test

# Apenas unitários
dotnet test tests/BillingLedger.Billing.UnitTests

# Teste específico
dotnet test --filter "FullyQualifiedName~Invoice_ShouldTransitionToPaid"

# Integração (requer Docker)
dotnet test tests/BillingLedger.IntegrationTests
```

## Deploy AWS (Milestone 3)

Consulte [`docs/runbook.md`](docs/runbook.md) para instruções completas de deploy, operação e troubleshooting em produção.

```bash
cd infra
npm install
npx cdk bootstrap
npx cdk deploy --all
```

## Decisões Arquiteturais

- **PostgreSQL schemas separados por BC**: um único cluster RDS, schemas `billing`, `payments`, `ledger`, `infra` — isolamento lógico sem overhead de múltiplos bancos
- **Outbox Pattern**: eventos publicados atomicamente na mesma transação da mudança de estado; dispatcher com `SKIP LOCKED` garante exactly-once delivery
- **MassTransit sobre SNS/SQS**: abstração que permite `InMemory` em testes e `SQS` em produção via feature flag `Messaging:Transport`
- **HMAC-SHA256 no webhook**: provedores externos autenticam via assinatura do payload; JWT não é usado em endpoints de webhook
- **Secrets Manager no ECS**: credenciais nunca em variáveis de ambiente em texto plano; injetadas via `secrets` no task definition

## Threat Model (resumo)

| Ameaça               | Mitigação                                                                      |
| -------------------- | ------------------------------------------------------------------------------ |
| Replay de pagamento  | Unique index `(provider, external_payment_id)` + idempotência no handler       |
| Double spending      | Transição de estado idempotente na Invoice; estado verificado antes de aplicar |
| Spoofing de webhook  | Validação de assinatura HMAC no endpoint `/payments/webhook`                   |
| Privilege escalation | JWT + RBAC via policies por endpoint; claims validados no middleware           |
| Exposição de erros   | ProblemDetails sem stack trace em produção; logs estruturados internos         |
