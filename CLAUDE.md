# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`billing_ledger` is an enterprise-grade event-driven billing/ledger backend built with .NET 9 / C#. It is a portfolio project targeting production-quality code.

## Tech Stack

- **Runtime**: .NET 9, C#
- **API**: ASP.NET Core Web API — **Controllers** (no Minimal APIs)
- **ORM**: EF Core + PostgreSQL (Testcontainers for tests, Docker for local, RDS in prod)
  - Single Postgres instance, **separate schemas per bounded context**: `billing`, `payments`, `ledger`, `infra`
- **Cache / Idempotency**: Redis (ElastiCache in prod)
- **Messaging**: AWS SNS → SQS (fanout), DLQ + redrive — via **MassTransit**
  - Local dev: **LocalStack SNS/SQS** (no in-memory transport)
- **Validation**: FluentValidation
- **Logging**: Serilog (structured, enriched with correlationId/userId/requestId)
- **Observability**: OpenTelemetry (traces + metrics)
- **Auth**: JWT — dev issuer locally, AWS Cognito in prod
- **IaC**: AWS CDK in **TypeScript** (`infra/`)
- **Local infra**: Docker Compose (Postgres, Redis, LocalStack)

## Repository Structure

```
billing_ledger/
├── src/
│   ├── BillingLedger.SharedKernel          # Primitives: Money, Result<T>, DomainEvent, EntityId
│   ├── BillingLedger.BuildingBlocks        # Cross-cutting: Outbox, Bus, OTel, Auth utils
│   ├── BillingLedger.Contracts             # Event contracts (versioned DTOs shared across BCs)
│   ├── BillingLedger.Billing.Api           # Billing bounded context + API host
│   ├── BillingLedger.Payments.Worker       # Payments bounded context + worker host
│   └── BillingLedger.Ledger.Worker         # Ledger bounded context + worker host
├── tests/
│   ├── BillingLedger.Billing.UnitTests
│   ├── BillingLedger.Payments.UnitTests
│   ├── BillingLedger.Ledger.UnitTests
│   └── BillingLedger.IntegrationTests      # Testcontainers: Postgres + Redis (+ optional LocalStack)
├── infra/                                  # AWS CDK project (TypeScript)
├── docs/                                   # ADRs, threat model, event catalog, runbook
├── docker-compose.yml
└── .github/workflows/                      # CI: build, test, dotnet format
```

Each bounded context follows a layered structure: `Domain` → `Application` → `Infrastructure`.

## Bounded Contexts

| Context | Aggregate | Role |
|---|---|---|
| Billing | `Invoice` | CRUD + issue/cancel, publishes events via Outbox |
| Payments | `PaymentAttempt` | Consumes external payment events, confirms payments |
| Ledger | `LedgerEntry` | Audit trail; every financial state change → ledger entry |
| Identity & Access | — | JWT RBAC: Admin / Finance / Support / ReadOnly |

## SAGA Flow

```
InvoiceIssued → PaymentReceived → PaymentConfirmed → InvoicePaid → LedgerEntryCreated
```
Overdue transitions are triggered by a scheduled background job/worker.

## Key Patterns (non-negotiable)

- **Outbox Pattern**: events are written to `OutboxMessages` within the same DB transaction as the aggregate change; a background dispatcher publishes them to SNS/SQS and sets `PublishedAt`.
- **Idempotency**: unique index on `(Provider, ExternalPaymentId)` in `PaymentAttempt`; handlers must be idempotent (e.g., already-Paid invoice → no-op).
- **DLQ + Retries**: MassTransit retry policies with backoff; failed messages go to DLQ with full traceability.
- **CorrelationId**: propagate from incoming header or generate; include in all logs and outgoing events.
- **ProblemDetails**: never expose internal errors — always return RFC 7807 ProblemDetails.

## Common Commands

```bash
# Start local infrastructure (Postgres, Redis, LocalStack)
docker compose up -d

# Restore + build
dotnet restore && dotnet build

# Run the API locally
dotnet run --project src/BillingLedger.Billing.Api

# Run all tests
dotnet test

# Run a single test project
dotnet test tests/BillingLedger.Billing.UnitTests

# Run a specific test by name
dotnet test --filter "FullyQualifiedName~Invoice_ShouldTransitionToPaid"

# Format / lint
dotnet format

# Apply EF Core migrations (must run for all three bounded contexts)
dotnet ef database update --project src/BillingLedger.Billing.Api
dotnet ef database update --project src/BillingLedger.Payments.Worker
dotnet ef database update --project src/BillingLedger.Ledger.Worker

# Add a new EF Core migration (specify which project owns the DbContext)
dotnet ef migrations add <MigrationName> --project src/BillingLedger.Billing.Api
```

## Developer Tools (`tools/`)

| File | Purpose |
|---|---|
| `setup.ps1` | One-shot local setup: Docker up → EF migrations → generate JWT tokens |
| `gen-token.csx` | Generate a dev JWT: `dotnet script tools/gen-token.csx <userId> <role>` (roles: `Finance`, `Admin`, `ReadOnly`, `Support`) |
| `api-requests.http` | REST Client (VS Code) HTTP scratch file for all endpoints |
| `send-webhook.ps1` | Simulate a payment webhook with HMAC-SHA256 signature |
| `billing_ledger.postman_collection.json` | Postman collection covering the full API surface |

Dev JWT signing key: `dev-signing-key-must-be-32-chars!!`, issuer `billing-ledger-dev`, audience `billing-ledger-api-dev`.

## API Endpoints (Billing.Api)

| Method | Path | Auth Role |
|---|---|---|
| POST | /api/invoices | Finance, Admin |
| POST | /api/invoices/{id}/issue | Finance, Admin |
| POST | /api/invoices/{id}/cancel | Finance, Admin |
| GET | /api/invoices/{id} | Support, Finance, Admin, ReadOnly |
| GET | /api/invoices | Support, Finance, Admin, ReadOnly |
| POST | /api/payments/webhook | Admin (simulated provider) |

## Event Contracts (BillingLedger.Contracts)

All events include `EventId`, `CorrelationId`, and `SchemaVersion = 1`.

- `InvoiceIssuedV1`
- `PaymentReceivedV1`
- `PaymentConfirmedV1`
- `InvoicePaidV1`
- `InvoiceOverdueV1`
- `LedgerEntryCreatedV1`

## Security Requirements

- Rate limiting per IP/token on all endpoints.
- Secrets via environment variables locally; AWS Secrets Manager in prod.
- Audit log (`AuditLog` table) for all sensitive actions: issue, cancel, simulate payment, status changes.
- Auth in dev: local JWT issuer configured via `appsettings.Development.json`; in prod, AWS Cognito OIDC.

## Testing Strategy

- **Unit tests**: domain rules (Invoice state machine), idempotency logic, event validation/serialization.
- **Integration tests**: Testcontainers (Postgres + Redis) + LocalStack for SQS/SNS.
- **Contract tests**: serialize/deserialize all V1 events to verify schema stability.

## AWS Infrastructure (CDK — `infra/`)

- VPC, RDS Postgres, ElastiCache Redis
- SNS Topic → SQS Queues (one per consumer) + DLQs with redrive policy
- ECS Fargate: `Billing.Api` (behind ALB), `Payments.Worker`, `Ledger.Worker`
- CloudWatch Logs + alarms (5xx rate, DLQ depth)

## MCP Usage Map

| Purpose | Preferred tools |
|---|---|
| Docs & APIs | `context7`, `Ref`, `awslabs-docs` |
| AWS messaging & infra | `awslabs-api`, `awslabs-cfn`, `awslabs-iam` |
| Observability validation | `arize-tracing-assistant` |
| Web research | `firecrawl-mcp`, `exa` |
| UI/browser debugging (Swagger/flows) | `chrome-devtools`, `playwright` |

**MCP reliability notes:**
- `awslabs-core` and `desktop-commander-in-docker` are currently failing — do not block tasks on them.
- Avoid DynamoDB scope unless explicitly requested.

## Agent Routing

| Agent | Responsibility |
|---|---|
| `tech-lead-orchestrator` | Milestone planning, task breakdown |
| `backend-architect` | DDD boundaries, application workflows, event contracts |
| `postgres-architect` | Schema design, indexes (idempotency/outbox), migrations |
| `security-hardening-validator` | JWT/Cognito, RBAC, audit logs, hardening checklist |
| `sre-observability` | OTel setup, correlationId propagation, logging structure |
| `qa-engineer` | Unit/integration/contract tests (Testcontainers + LocalStack) |
| `code-quality-reviewer` | Final review, conventions, production readiness |

## Git Workflow (obrigatório)

Após **qualquer** conjunto de mudanças funcionais (bug fix, feature, config, docs):
1. `dotnet build` — zero erros
2. `git add` nos arquivos modificados
3. `git commit -m "tipo(escopo): descrição concisa"`
4. `git push origin main`

Não acumule mudanças: commit + push ao final de cada tarefa ou bloco lógico de alterações.

## Definition of Done (every PR)

- `dotnet test` passes (unit + integration where applicable)
- `dotnet format` clean
- All public endpoints return RFC 7807 ProblemDetails on errors
- CorrelationId propagated: incoming request → logs → outgoing events
- Event contracts V1: serialization/deserialization tests pass; `schemaVersion` enforced
- Outbox: events written within DB transaction; dispatcher marks `PublishedAt`
- Idempotency: unique DB constraints in place; handlers are no-ops when already processed
- RBAC: policies enforced on all endpoints; at least one integration test covering forbidden/allowed roles
