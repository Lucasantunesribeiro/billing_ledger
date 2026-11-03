# ADR 0003 - Ledger append-only com valores decimais

Status: aceita

## Contexto
O ledger e a trilha financeira do sistema; alterar registros passados impede auditoria.

## Decisao
`LedgerEntry` nao expoe setters publicos e so e criado por `LedgerEntry.Create`. Cada fatura emitida gera um lancamento `Debit` e cada fatura paga gera um `Credit`. Valores usam `decimal` (coluna `numeric(18,2)`), nunca ponto flutuante. Alem do ledger, `infra.audit_logs` registra acoes da API.

## Consequencias
- Correcao de um lancamento deve ser feita por novo lancamento (estorno); essa operacao ainda nao existe no codigo.
- O modelo atual e de lancamento unico por evento, nao de partidas dobradas com duas pernas por transacao.
