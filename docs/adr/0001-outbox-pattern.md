# ADR 0001 - Outbox Pattern para publicacao de eventos

Status: aceita

## Contexto
Mudar o estado da fatura e publicar o evento no broker sao duas operacoes em sistemas distintos. Sem coordenacao, uma falha entre elas perde ou duplica eventos.

## Decisao
O `DomainEventToOutboxInterceptor` grava os eventos de dominio na tabela `infra.outbox_messages` na mesma transacao da mudanca de estado. O `OutboxDispatcherService` le as mensagens pendentes e as publica via `IEventBus`.

## Consequencias
- Entrega "pelo menos uma vez": consumidores precisam ser idempotentes (ver ADR 0002).
- Atomicidade entre estado e evento; testada em `OutboxAtomicityTests`.
