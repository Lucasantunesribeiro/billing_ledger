using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BillingLedger.Contracts.Billing;
using BillingLedger.Contracts.Ledger;
using BillingLedger.Contracts.Payments;
using BillingLedger.SharedKernel.Primitives;
using FluentAssertions;
using MassTransit.Serialization;

namespace BillingLedger.Billing.UnitTests.Contracts;

/// <summary>
/// Os contratos trafegam via Outbox (JsonSerializer padrao) e via MassTransit
/// (SystemTextJsonMessageSerializer). Valores decimais precisam sobreviver ao round-trip
/// sem passar por double.
/// </summary>
public class ContractDecimalSerializationTests
{
    public static TheoryData<string> Amounts => new()
    {
        "0.01",
        "10.10",
        "150.35",
        "999999999.99",
        "9999999999999999.99", // limite de numeric(18,2)
        "79228162514264337593543950335", // decimal.MaxValue
        "0.30000000000000004" // em double viraria 0.3 ao converter para decimal
    };

    private static readonly string[] Serializers = ["outbox-default", "masstransit"];

    public static TheoryData<string> SerializerNames => new(Serializers);

    private static JsonSerializerOptions OptionsFor(string name) => name switch
    {
        "outbox-default" => JsonSerializerOptions.Default,
        "masstransit" => SystemTextJsonMessageSerializer.Options,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static decimal D(string raw) => decimal.Parse(raw, CultureInfo.InvariantCulture);

    [Theory]
    [MemberData(nameof(Amounts))]
    public void InvoiceIssuedV1_RoundTrip_ShouldPreserveExactDecimal(string raw)
    {
        foreach (var serializer in Serializers)
        {
            var options = OptionsFor(serializer);
            var original = new InvoiceIssuedV1
            {
                InvoiceId = Guid.NewGuid(),
                CustomerId = Guid.NewGuid(),
                Amount = D(raw),
                Currency = "BRL",
                DueDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                IssuedAt = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc),
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonSerializer.Serialize(original, options);
            var back = JsonSerializer.Deserialize<InvoiceIssuedV1>(json, options)!;

            back.Should().Be(original, $"round-trip via {serializer}");
            back.Amount.ToString(CultureInfo.InvariantCulture).Should().Be(raw, "escala (casas decimais) preservada");
            back.EventId.Should().Be(original.EventId, "EventId e a chave de idempotencia e nao pode mudar no round-trip");
        }
    }

    [Theory]
    [InlineData("outbox-default", JsonValueKind.Number, "1234567890123456.78")]
    // O MassTransit 8 serializa decimal como string JSON ("1234567890123456.78"): sem perda e
    // seguro para consumidores que leem numeros como double (ex.: JavaScript).
    [InlineData("masstransit", JsonValueKind.String, "\"1234567890123456.78\"")]
    public void Amount_ShouldBeWrittenWithExactDigits_NeverAsDoubleOrExponent(
        string serializer, JsonValueKind expectedKind, string expectedRaw)
    {
        var message = new InvoicePaidV1
        {
            InvoiceId = Guid.NewGuid(),
            Amount = 1234567890123456.78m,
            Currency = "BRL",
            PaidAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(message, OptionsFor(serializer));
        using var doc = JsonDocument.Parse(json);
        var amount = doc.RootElement.EnumerateObject()
            .Single(p => p.Name.Equals("amount", StringComparison.OrdinalIgnoreCase)).Value;

        amount.ValueKind.Should().Be(expectedKind);
        amount.GetRawText().Should().Be(expectedRaw);
    }

    [Fact]
    public void OutboxPayload_ShouldBeReadableByMassTransitSerializer()
    {
        // Outbox grava com JsonSerializer padrao (numero); o dispatcher desserializa e o
        // MassTransit reserializa (string). Os dois formatos precisam ser aceitos sem perda.
        var original = new InvoicePaidV1
        {
            InvoiceId = Guid.NewGuid(),
            Amount = 9999999999999999.99m,
            Currency = "BRL",
            PaidAt = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
        };

        var outboxJson = JsonSerializer.Serialize(original);
        var viaMassTransit = JsonSerializer.Deserialize<InvoicePaidV1>(outboxJson, SystemTextJsonMessageSerializer.Options)!;
        var massTransitJson = JsonSerializer.Serialize(viaMassTransit, SystemTextJsonMessageSerializer.Options);
        var back = JsonSerializer.Deserialize<InvoicePaidV1>(massTransitJson, SystemTextJsonMessageSerializer.Options)!;

        back.Should().Be(original);
    }

    [Fact]
    public void Deserialize_ShouldNotLosePrecisionThroughDouble()
    {
        const string json = """{"InvoiceId":"7d3c7f8e-0000-0000-0000-000000000001","Amount":0.30000000000000004,"Currency":"BRL","PaidAt":"2026-11-03T00:00:00Z"}""";

        var msg = JsonSerializer.Deserialize<InvoicePaidV1>(json)!;

        msg.Amount.Should().Be(0.30000000000000004m);
        msg.Amount.Should().NotBe((decimal)0.30000000000000004d);
    }

    [Theory]
    [MemberData(nameof(SerializerNames))]
    public void PaymentContracts_RoundTrip_ShouldPreserveAmountAndCurrency(string serializer)
    {
        var options = OptionsFor(serializer);
        var received = new PaymentReceivedV1
        {
            InvoiceId = Guid.NewGuid(),
            ExternalPaymentId = "pix-1",
            Provider = "PIX",
            Amount = 0.10m,
            Currency = "BRL",
            ReceivedAt = DateTime.UtcNow
        };
        var confirmed = new PaymentConfirmedV1
        {
            InvoiceId = received.InvoiceId,
            ExternalPaymentId = "pix-1",
            Amount = 0.10m,
            Currency = "BRL",
            ConfirmedAt = DateTime.UtcNow
        };
        var ledger = new LedgerEntryCreatedV1
        {
            LedgerEntryId = Guid.NewGuid(),
            InvoiceId = received.InvoiceId,
            Type = "Credit",
            Amount = 0.10m,
            Currency = "BRL",
            OccurredAt = DateTime.UtcNow
        };

        JsonSerializer.Deserialize<PaymentReceivedV1>(JsonSerializer.Serialize(received, options), options)
            .Should().Be(received);
        JsonSerializer.Deserialize<PaymentConfirmedV1>(JsonSerializer.Serialize(confirmed, options), options)
            .Should().Be(confirmed);
        JsonSerializer.Deserialize<LedgerEntryCreatedV1>(JsonSerializer.Serialize(ledger, options), options)
            .Should().Be(ledger);
    }

    [Fact]
    public void PaymentConfirmedV1_LegacyPayloadWithoutAmount_ShouldStillDeserialize()
    {
        const string legacy = """{"SchemaVersion":1,"EventId":"7d3c7f8e-0000-0000-0000-000000000002","InvoiceId":"7d3c7f8e-0000-0000-0000-000000000003","ExternalPaymentId":"pix-old","ConfirmedAt":"2026-01-01T00:00:00Z"}""";

        var msg = JsonSerializer.Deserialize<PaymentConfirmedV1>(legacy)!;

        msg.Amount.Should().BeNull();
        msg.Currency.Should().BeNull();
    }

    [Fact]
    public void MonetaryTypes_ShouldNeverUseFloatingPoint()
    {
        var assemblies = new[]
        {
            typeof(InvoiceIssuedV1).Assembly,                 // Contracts
            typeof(Money).Assembly,                           // SharedKernel
            typeof(BillingLedger.Billing.Api.Domain.Aggregates.Invoice).Assembly
        };
        var floating = new[] { typeof(double), typeof(float), typeof(Half), typeof(double?), typeof(float?) };

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace?.StartsWith("BillingLedger", StringComparison.Ordinal) == true)
            // Configuracao de resiliencia (ratio do circuit breaker) nao e valor monetario.
            .Where(t => !t.Namespace!.Contains(".Infrastructure.Resilience", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(p => (Type: t, Property: p)))
            .Where(x => floating.Contains(x.Property.PropertyType))
            .Select(x => $"{x.Type.FullName}.{x.Property.Name}");

        offenders.Should().BeEmpty("valores monetarios devem ser decimal");
    }
}
