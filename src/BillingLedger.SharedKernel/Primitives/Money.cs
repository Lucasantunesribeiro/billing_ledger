using System.Globalization;

namespace BillingLedger.SharedKernel.Primitives;

/// <summary>
/// Valor monetario: sempre <see cref="decimal"/> (nunca double/float) com moeda ISO 4217 explicita.
///
/// Regras (ver docs/adr/0004-dinheiro-decimal-escala-e-arredondamento.md):
///   - Escala fixa de <see cref="Scale"/> casas decimais, igual a coluna numeric(18,2) do banco.
///     <see cref="Of"/> REJEITA valores com mais casas em vez de arredondar silenciosamente.
///   - Arredondamento so acontece de forma explicita via <see cref="Round"/>, com
///     <see cref="DefaultRounding"/> (MidpointRounding.ToEven, "arredondamento bancario").
///   - Rateio via <see cref="Allocate(int)"/> nao perde nem cria centavos: o residuo e
///     distribuido um centavo por vez nas primeiras parcelas.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>Casas decimais suportadas (centavos). Igual a precisao numeric(18,2) das tabelas.</summary>
    public const int Scale = 2;

    /// <summary>Maior valor que cabe em numeric(18,2): 16 digitos inteiros.</summary>
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    /// <summary>Modo de arredondamento padrao do sistema (bancario / half-even).</summary>
    public const MidpointRounding DefaultRounding = MidpointRounding.ToEven;

    private const decimal MinorUnit = 0.01m;

    public static Money Of(decimal amount, string currency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amount, MaxAmount);
        if (!HasValidScale(amount))
            throw new ArgumentException(
                $"Amount {amount.ToString(CultureInfo.InvariantCulture)} has more than {Scale} decimal places. " +
                "Round explicitly with Money.Round before creating Money.", nameof(amount));

        return new Money(amount, NormalizeCurrency(currency));
    }

    /// <summary>Zero na moeda informada (elemento neutro para somas).</summary>
    public static Money Zero(string currency) => new(0m, NormalizeCurrency(currency));

    /// <summary>
    /// Arredonda explicitamente para <see cref="Scale"/> casas. Use para valores calculados
    /// (juros, percentuais, conversao) antes de criar <see cref="Money"/>.
    /// </summary>
    public static decimal Round(decimal amount, MidpointRounding mode = DefaultRounding) =>
        decimal.Round(amount, Scale, mode);

    /// <summary>True se o valor tem no maximo <see cref="Scale"/> casas decimais significativas.</summary>
    public static bool HasValidScale(decimal amount) => decimal.Round(amount, Scale) == amount;

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    /// <summary>Soma exata de muitos valores da mesma moeda (sem acumulo de erro de ponto flutuante).</summary>
    public static Money Sum(IEnumerable<Money> values, string currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        var total = Zero(currency);
        foreach (var value in values)
            total = total.Add(value);
        return total;
    }

    /// <summary>
    /// Divide o valor em <paramref name="parts"/> parcelas que somam exatamente o original.
    /// Ex.: 100,00 / 3 = [33,34; 33,33; 33,33] — o centavo residual vai para a primeira parcela.
    /// </summary>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parts);
        return Allocate(Enumerable.Repeat(1, parts).ToArray());
    }

    /// <summary>
    /// Rateio proporcional aos pesos informados. Cada parcela e truncada para centavos e o
    /// residuo e distribuido um centavo por vez, na ordem, ate fechar o total exato.
    /// </summary>
    public IReadOnlyList<Money> Allocate(params int[] ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        if (ratios.Length == 0)
            throw new ArgumentException("At least one ratio is required.", nameof(ratios));
        if (ratios.Any(r => r < 0))
            throw new ArgumentOutOfRangeException(nameof(ratios), "Ratios must be non-negative.");
        // Money pode ser construido direto pelo construtor do record; protege o algoritmo
        // contra valores negativos ou com mais de 2 casas (o residuo nao fecharia em centavos).
        if (Amount < 0m || !HasValidScale(Amount))
            throw new InvalidOperationException(
                $"Cannot allocate {this}: amount must be non-negative with at most {Scale} decimal places.");

        long totalRatio = ratios.Sum(r => (long)r);
        if (totalRatio == 0)
            throw new ArgumentException("Sum of ratios must be greater than zero.", nameof(ratios));

        var shares = new decimal[ratios.Length];
        var allocated = 0m;
        for (var i = 0; i < ratios.Length; i++)
        {
            // Trunca (ToZero) para nunca alocar mais que o total; o residuo e tratado abaixo.
            shares[i] = decimal.Round(Amount * ratios[i] / totalRatio, Scale, MidpointRounding.ToZero);
            allocated += shares[i];
        }

        var remainder = Amount - allocated;
        for (var i = 0; remainder > 0m; i = (i + 1) % shares.Length)
        {
            if (ratios[i] == 0) continue;
            shares[i] += MinorUnit;
            remainder -= MinorUnit;
        }

        var currency = Currency;
        return shares.Select(s => new Money(s, currency)).ToArray();
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}");
    }

    private static string NormalizeCurrency(string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(c => c is >= 'A' and <= 'Z'))
            throw new ArgumentException(
                $"Currency '{currency}' must be a 3-letter ISO 4217 code (e.g. BRL, USD).", nameof(currency));
        return normalized;
    }

    public override string ToString() =>
        $"{Amount.ToString("F2", CultureInfo.InvariantCulture)} {Currency}";
}
