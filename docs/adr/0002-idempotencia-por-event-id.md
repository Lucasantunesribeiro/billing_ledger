# ADR 0002 - Idempotencia por EventId e por pagamento externo

Status: aceita

## Contexto
Com entrega "pelo menos uma vez" (SNS/SQS + Outbox) e webhooks reenviados por provedores, o mesmo evento chega mais de uma vez.

## Decisao
- Ledger: cada `LedgerEntry` guarda o `EventId` do evento de origem, com indice unico `uq_ledger_entries_event_id`. Os consumidores fazem uma checagem previa e tratam a violacao de unicidade (SQLSTATE 23505) como duplicata ja processada.
- Pagamentos: indice unico em `(provider, external_payment_id)`.

## Consequencias
- Escritas concorrentes do mesmo evento nao geram lancamento duplicado, pois a garantia final e do banco.
- A chave de idempotencia e o `EventId` do evento, nao uma chave enviada pelo cliente HTTP.
