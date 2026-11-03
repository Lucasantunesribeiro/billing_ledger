# ADR 0004 - Dinheiro em decimal: escala, moeda e arredondamento explicitos

Status: aceita

## Contexto
Valores monetarios passam por quatro fronteiras: HTTP (JSON do cliente e do webhook), dominio (`Money`),
banco (colunas `numeric(18,2)` em `billing.invoices`, `payments.payment_attempts` e `ledger.ledger_entries`)
e mensageria (Outbox com `System.Text.Json` padrao e MassTransit com `SystemTextJsonMessageSerializer`).

Antes desta decisao:
- `Money.Of` aceitava qualquer numero de casas decimais. Um valor como `10.005` era aceito pela API,
  arredondado silenciosamente pelo PostgreSQL para `10.01` (ou `10.00`) na gravacao, enquanto o evento
  `InvoiceIssuedV1` gravado no Outbox carregava `10.005`. Banco e ledger divergiam.
- Fatura de valor zero era aceita (`GreaterThanOrEqualTo(0)`), gerando lancamento de ledger sem efeito.
- `PaymentConfirmedV1` nao carregava valor: qualquer pagamento (inclusive parcial) quitava a fatura e o
  ledger registrava um `Credit` do valor cheio.
- A moeda era uma `string` livre, sem validacao de formato.

## Decisao
1. **Sempre `decimal`.** Nenhum tipo monetario usa `double`/`float`; ha teste que varre os assemblies
   `Contracts`, `SharedKernel` e `Billing.Api` e falha se surgir propriedade de ponto flutuante fora da
   configuracao de resiliencia.
2. **Escala fixa de 2 casas (`Money.Scale`), igual a `numeric(18,2)`.** `Money.Of`, `LedgerEntry.Create`
   e os validadores HTTP **rejeitam** valores com mais de 2 casas significativas, em vez de arredondar.
   `10.100` e aceito (so 2 casas significativas); `10.005` e rejeitado com 400. O teto e
   `Money.MaxAmount = 9_999_999_999_999_999.99` (capacidade de `numeric(18,2)`); a API limita ainda mais
   (`999_999_999`).
3. **Arredondamento so explicito**, via `Money.Round(valor, modo)`. O modo padrao e
   `MidpointRounding.ToEven` (arredondamento bancario: `0.125 -> 0.12`, `0.135 -> 0.14`), que nao
   introduz vies sistematico em grandes volumes. Quem precisar de outra regra (ex.: `AwayFromZero` exigido
   por contrato ou legislacao) passa o modo explicitamente. Valores calculados (juros, percentuais,
   conversao) devem ser arredondados antes de virar `Money`.
4. **Rateio sem perder nem criar centavos** (`Money.Allocate`): cada parcela e truncada para centavos e o
   residuo e distribuido um centavo por vez, na ordem, nas parcelas com peso maior que zero.
   `100,00 / 3 = [33,34; 33,33; 33,33]`.
5. **Moeda explicita.** `Money` exige codigo ISO 4217 de 3 letras (normalizado para maiusculas); somas e
   saldos com moedas diferentes lancam excecao. `LedgerEntry` reaproveita essa validacao.
6. **Valor sempre positivo nos lancamentos.** O sinal vem do `Type` (`Debit`/`Credit`); `LedgerEntry`
   rejeita `Amount <= 0`. Fatura (`Invoice.Create`) tambem rejeita valor zero.
7. **Contratos sem perda.** `PaymentReceivedV1` e `PaymentConfirmedV1` ganham `Currency` (e
   `PaymentConfirmedV1` ganha `Amount`) como campos opcionais e aditivos ao V1. O Billing so marca a
   fatura como paga se `Amount` e `Currency` baterem exatamente com a fatura; quando a moeda vem nula,
   assume-se a moeda da fatura; mensagens antigas sem `Amount` mantem o comportamento anterior (com log).
   O Outbox grava `decimal` como numero JSON com todos os digitos e o MassTransit 8 o reserializa como
   string JSON; os dois formatos sao lidos sem passar por `double` (coberto por testes de round-trip).

## Consequencias
- Divergencia banco x evento por arredondamento implicito deixa de ser possivel pelos caminhos da API.
- Pagamento parcial, excedente ou em outra moeda nao quita a fatura: e ACK com log de aviso e a tentativa
  fica em `payments.payment_attempts` para conciliacao manual. Nao ha fluxo automatico de conciliacao,
  estorno ou pagamento parcial.
- Moedas com 0 ou 3 casas decimais (JPY, KWD, BHD) nao sao representadas corretamente com escala fixa 2;
  suportar isso exigiria escala por moeda e migration das colunas.
- `Money` continua sendo um `record struct`: o construtor posicional nao valida. Codigo de dominio deve
  usar `Money.Of`; `Allocate` se protege contra instancias invalidas.
