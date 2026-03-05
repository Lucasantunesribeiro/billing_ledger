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

> Commands below assume the project is initialized. Adjust paths as needed.

```bash
# Restore dependencies
dotnet restore

# Build entire solution
dotnet build

# Run all tests
dotnet test

# Run a single test project
dotnet test tests/BillingLedger.Billing.UnitTests

# Run a specific test by name
dotnet test --filter "FullyQualifiedName~Invoice_ShouldTransitionToPaid"

# Apply EF Core migrations
dotnet ef database update --project src/BillingLedger.Billing.Api

# Add a new EF Core migration
dotnet ef migrations add <MigrationName> --project src/BillingLedger.Billing.Api

# Format / lint
dotnet format

# Start local infrastructure
docker compose up -d

# Stop local infrastructure
docker compose down
```

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

## Milestones

1. **MVP**: Invoice CRUD + issue (Outbox) + webhook simulation + full SAGA (workers)
2. **Enterprise**: Idempotency + DLQ/retries + RBAC + audit + OTel/correlationId
3. **AWS**: CDK deploy + ECS/RDS/SQS/SNS/Redis + runbook

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

## Definition of Done (every PR)

- `dotnet test` passes (unit + integration where applicable)
- `dotnet format` clean
- All public endpoints return RFC 7807 ProblemDetails on errors
- CorrelationId propagated: incoming request → logs → outgoing events
- Event contracts V1: serialization/deserialization tests pass; `schemaVersion` enforced
- Outbox: events written within DB transaction; dispatcher marks `PublishedAt`
- Idempotency: unique DB constraints in place; handlers are no-ops when already processed
- RBAC: policies enforced on all endpoints; at least one integration test covering forbidden/allowed roles
