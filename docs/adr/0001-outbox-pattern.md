# ADR 0001 - Outbox Pattern para publicacao de eventos

Status: aceita

## Contexto
Mudar o estado da fatura e publicar o evento no broker sao duas operacoes em sistemas distintos. Sem coordenacao, uma falha entre elas perde ou duplica eventos.

## Decisao
O `DomainEventToOutboxInterceptor` grava os eventos de dominio na tabela `infra.outbox_messages` na mesma transacao da mudanca de estado. O `OutboxDispatcherService` le as mensagens pendentes com `SELECT ... FOR UPDATE SKIP LOCKED` e as publica via `IEventBus`. O SELECT, a publicacao e a gravacao de `published_at` ficam na mesma transacao: sem ela o lock seria liberado logo apos o SELECT (autocommit) e duas instancias publicariam a mesma linha.

## Consequencias
- Entrega "pelo menos uma vez": consumidores precisam ser idempotentes (ver ADR 0002).
- Atomicidade entre estado e evento; testada em `OutboxAtomicityTests`.
- Instancias paralelas do dispatcher nao publicam a mesma linha (`OutboxConcurrentDispatchTests`). Se a publicacao der certo e o commit falhar, a linha e republicada com o mesmo payload e, portanto, o mesmo `EventId`.
- A transacao fica aberta durante a publicacao do lote (ate 20 mensagens, incluindo os retries do `ResilientEventBus`).
