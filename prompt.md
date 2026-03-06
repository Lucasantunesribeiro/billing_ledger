Você é um Arquiteto de Software Sênior (.NET/C#) e vai criar um projeto BACKEND enterprise-grade chamado `billing_ledger`.

OBJETIVO (1 linha)
Construir um backend corporativo event-driven que emite cobranças (invoices), recebe eventos assíncronos de pagamento (PIX/boleto/cartão SIMULADOS), concilia transações e mantém um ledger (razão) consistente — com DDD, mensageria real na AWS, Outbox Pattern, idempotência, DLQ/retries, e segurança (JWT + RBAC + auditoria).

CONTEXTO / CONSTRAINTS
- Público-alvo do portfólio: estágio/jr, porém com qualidade “produção”.
- Projeto grande, foco total no BACKEND (sem front).
- Cloud principal: AWS.
- Domínio enterprise (financeiro/cobrança/conciliação).
- Mensageria real obrigatória.
- Prioridades: Arquitetura/DDD e Segurança.
- Evitar complexidade desnecessária: preferir uma “solução única” (monorepo) com múltiplos hosts (API + Workers), mas com separação clara (bounded contexts + camadas).

STACK E DECISÕES TÉCNICAS (obrigatório)
- .NET 9 (ou .NET 8 LTS se justificar) + C#
- ASP.NET Core Web API + Minimal APIs ou Controllers (escolha e padronize)
- EF Core + PostgreSQL (RDS em prod; Postgres container no local)
- Redis (cache e idempotência)
- Mensageria AWS:
  - Preferência: SNS -> SQS (fanout), com DLQ e redrive policy
  - Opcional: RabbitMQ (não usar se aumentar muito o escopo)
- Biblioteca de mensageria: MassTransit (com transporte SQS/SNS se viável)
- Logs estruturados: Serilog
- Observabilidade: OpenTelemetry (traces/metrics) + correlationId
- Validação: FluentValidation
- Autenticação: JWT (AWS Cognito em prod); para DEV, um “dev issuer” local ou Cognito Local/LocalStack se possível
- Infra as Code: AWS CDK (em C# ou TypeScript, escolha 1 e padronize)
- Docker: docker compose para ambiente local (Postgres, Redis, LocalStack se usar SQS/SNS local)

ARQUITETURA (obrigatório)
1) DDD + Bounded Contexts:
   - Billing (Cobrança): Aggregate `Invoice`
     Status: Draft -> Issued -> Paid / Overdue / Cancelled
     Regras: vencimento, juros/multa simples, reemissão (opcional)
   - Payments (Processamento): Aggregate `PaymentAttempt`
     Consome eventos “externos” simulados e publica eventos internos
   - Ledger (Razão/Conciliação): Aggregate `LedgerEntry`
     Toda mudança financeira gera lançamento auditável
   - Identity & Access: RBAC (Admin/Finance/Support/ReadOnly) + auditoria

2) Padrões obrigatórios:
   - Outbox Pattern: evento só sai após commit no banco
   - Idempotência: evitar duplicidade de pagamento/evento (chave externa única)
   - Retries + DLQ: reprocessamento controlado + rastreabilidade
   - SAGA simples (orquestração por eventos):
     InvoiceIssued -> PaymentReceived -> PaymentConfirmed -> InvoicePaid -> LedgerEntryCreated
     (Overdue por job agendado ou worker periódico)

3) Topologia:
   - Um repositório, uma solution, múltiplos projetos executáveis:
     - src/BillingLedger.Billing.Api
     - src/BillingLedger.Payments.Worker
     - src/BillingLedger.Ledger.Worker
     - src/BillingLedger.Contracts (eventos/contratos)
     - src/BillingLedger.BuildingBlocks (infra comum: outbox, bus, observability, auth utils)
     - src/BillingLedger.SharedKernel (primitivos: Money, Result, DomainEvent, EntityId)
   - Cada BC tem: Domain, Application, Infrastructure (camadas) + testes.

ENTIDADES (modelagem mínima)
- Invoice:
  - Id (GUID/ULID), CustomerId, Amount (Money), Currency, DueDate, Status
  - IssuedAt, PaidAt, CancelledAt
  - ExternalReference (ex: “INV-2026-0001”)
- PaymentAttempt:
  - Id, InvoiceId, Provider (PIX/BOLETO/CARD), ExternalPaymentId (único), Amount, Status
  - ReceivedAt, ConfirmedAt, RawPayloadHash (opcional)
- LedgerEntry:
  - Id, InvoiceId, PaymentAttemptId (opcional), Type (Debit/Credit), Amount, OccurredAt
  - CorrelationId, Description
- OutboxMessage:
  - Id, OccurredAt, Type, Payload, CorrelationId, PublishedAt (nullable), Attempts
- AuditLog:
  - Id, ActorUserId, Action, ResourceType, ResourceId, Ip, UserAgent, At, CorrelationId

CONTRATOS DE EVENTOS (obrigatório)
Defina os eventos como JSON (e classes C#), com versionamento simples:
- InvoiceIssuedV1 { invoiceId, customerId, amount, currency, dueDate, issuedAt, correlationId }
- PaymentReceivedV1 { invoiceId, externalPaymentId, provider, amount, receivedAt, correlationId }
- PaymentConfirmedV1 { invoiceId, externalPaymentId, confirmedAt, correlationId }
- InvoicePaidV1 { invoiceId, paidAt, correlationId }
- InvoiceOverdueV1 { invoiceId, overdueAt, correlationId }
- LedgerEntryCreatedV1 { ledgerEntryId, invoiceId, type, amount, occurredAt, correlationId }
Regras:
- Sempre incluir correlationId e eventId
- Incluir schemaVersion (ex: 1)
- Campos obrigatórios e validação

API (Billing.API) — endpoints mínimos
- POST /api/invoices (criar draft)
- POST /api/invoices/{id}/issue (emite e publica InvoiceIssued via Outbox)
- POST /api/invoices/{id}/cancel
- GET  /api/invoices/{id}
- GET  /api/invoices?status=&from=&to=&customerId=
- (Opcional) POST /api/payments/webhook (simulador de provedor):
   - recebe payload de pagamento e publica PaymentReceived no tópico SNS (ou fila)
Observação: Documentar com Swagger + exemplos.

WORKERS (mensageria)
- Payments.Worker:
  - Consome PaymentReceived
  - Valida, aplica idempotência (ExternalPaymentId unique)
  - Pode simular “confirmar” (PaymentConfirmed) após checagens
  - Publica PaymentConfirmed
- Billing (na API ou worker separado):
  - Ao receber PaymentConfirmed, marca Invoice como Paid usando handler (com idempotência)
  - Publica InvoicePaid (via Outbox)
- Ledger.Worker:
  - Consome InvoicePaid e cria LedgerEntry (Credit)
  - Consome InvoiceIssued e cria LedgerEntry (Debit) se fizer sentido (ou só no pagamento; documente a decisão)

IDEMPOTÊNCIA (obrigatório)
- Garantir que PaymentReceived duplicado não gera duplicidade:
  - Unique index: (provider, externalPaymentId)
  - Tabela “ProcessedMessages” ou usar a própria PaymentAttempt com unique
- Garantir que handlers sejam idempotentes (ex: invoice já está Paid -> ignorar)

OUTBOX (obrigatório)
- Em qualquer mudança de estado que publica evento:
  - Salvar evento na Outbox dentro da mesma transaction do aggregate
- Implementar dispatcher:
  - background service que publica eventos pendentes no bus e marca PublishedAt
  - retries com backoff e limite de tentativas
  - registrar attempts e erros

SEGURANÇA (obrigatório)
- JWT Auth com:
  - Prod: AWS Cognito (documentar setup)
  - Dev: configuração simples para emitir tokens (dev issuer) e validar
- RBAC:
  - Admin: tudo
  - Finance: emitir/cancelar/consultar e ver ledger
  - Support: consultar invoice e status
  - ReadOnly: somente GET
- Auditoria:
  - Logar ações sensíveis: emitir/cancelar, simular pagamento, alterações de status
- Hardening:
  - Rate limiting por IP/token
  - Validation em todos inputs
  - Não expor erros internos (ProblemDetails)
  - Segredos via env/Secrets Manager em prod

OBSERVABILIDADE (obrigatório)
- CorrelationId:
  - Se vier header, propagar; se não vier, gerar
  - Propagar correlationId em logs e eventos
- OpenTelemetry:
  - traces para requests e consumers
- Logs estruturados:
  - Serilog com enrichment (correlationId, requestId, userId)

TESTES (obrigatório)
- Unit tests:
  - Regras de domínio do Invoice (transições de estado)
  - Idempotência e validações de eventos
- Integration tests (preferência Testcontainers):
  - Postgres + Redis
  - (Opcional) LocalStack para SQS/SNS, se viável
- Contratos:
  - Teste de serialização/desserialização de eventos (schema V1)

CI/CD (obrigatório)
- GitHub Actions:
  - build + test
  - lint/format (dotnet format)
  - publish docker images (opcional)
- Badge no README

INFRA (CDK) — mínimo em prod
- VPC (pode usar default pra simplificar, mas documente)
- RDS Postgres
- ElastiCache Redis
- SNS Topic + SQS Queues + DLQ + redrive
- ECS Fargate services:
  - Billing.API (ALB)
  - Payments.Worker
  - Ledger.Worker
- CloudWatch Logs + alarm simples (erros 5xx, DLQ depth)

DOCKER COMPOSE (local)
- postgres
- redis
- localstack (opcional) ou modo “in-memory transport” só no local (mas documente diferença)
- scripts de migration (EF Core)

DELIVERABLES (obrigatório)
1) Repositório completo com:
   - /src, /tests, /infra
   - README.md excelente
   - docs/ (ADRs, threat-model, arquitetura, event-catalog, runbook)
2) README deve conter:
   - Visão geral e arquitetura (inclua diagrama Mermaid)
   - Como rodar local (passo-a-passo)
   - Catálogo de eventos (tabela)
   - Decisões arquiteturais (ADRs)
   - Threat model (replay, spoofing, double spending, privilege escalation) + mitigação
   - Como fazer deploy na AWS (CDK)
3) Postman collection / examples de curl

MILESTONES (definir e cumprir)
- Milestone 1 (MVP):
  - CRUD + issue de invoices (com Outbox)
  - Webhook simulado (ou publicação direta)
  - Workers processam PaymentReceived -> PaymentConfirmed -> InvoicePaid -> LedgerEntryCreated
- Milestone 2 (Enterprise):
  - Idempotência completa + DLQ/retries + auditoria + RBAC
  - Observabilidade com correlationId e OTel
- Milestone 3 (AWS):
  - CDK + ECS/RDS/SQS/SNS/Redis
  - runbook de deploy e troubleshooting

REQUISITOS DE QUALIDADE
- Código limpo, SOLID, separação de camadas, exceptions tratadas
- Sem gambiarra: documentar trade-offs
- API consistente, status codes corretos, ProblemDetails
- Migrations e seed (opcional)

SAÍDA ESPERADA DO SEU TRABALHO (responda com)
1) Estrutura de pastas do repositório (árvore)
2) Backlog detalhado (épicos -> histórias -> critérios de aceitação)
3) Diagramas Mermaid (contexto + fluxo de eventos)
4) Contratos de eventos (classes C# + JSON exemplo)
5) Esqueleto de código por projeto (program.cs/config + 1 exemplo completo de handler/aggregate/outbox)
6) Migrations/DDL sugerido (tabelas principais)
7) README.md completo + docs/ADRs + threat model
8) Pipeline GitHub Actions (YAML)
9) docker-compose.yml local
10) CDK mínimo (infra/) com instruções de deploy

IMPORTANTE
- Mantenha o escopo realista para 1 projeto grande de portfólio.
- Priorize SNS->SQS + Postgres + Redis + Outbox + idempotência + RBAC.
- Se algo ficar opcional, marque claramente como OPTIONAL.