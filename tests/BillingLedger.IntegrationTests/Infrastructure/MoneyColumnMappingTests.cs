using BillingLedger.Billing.Api.Infrastructure.Persistence;
using BillingLedger.Billing.Api.Infrastructure.Persistence.Interceptors;
using BillingLedger.Ledger.Worker.Infrastructure.Persistence;
using BillingLedger.Payments.Worker.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BillingLedger.IntegrationTests.Infrastructure;

/// <summary>
/// Garante que toda propriedade decimal dos tres bounded contexts mapeia para numeric(18,2)
/// e que o modelo EF nao tem mudancas pendentes em relacao as migrations (nao precisa de banco).
/// </summary>
public class MoneyColumnMappingTests
{
    private const string FakeConnection = "Host=localhost;Database=model_only;Username=x;Password=x";

    public static TheoryData<string> Contexts => new() { "billing", "ledger", "payments" };

    private static DbContext Create(string name) => name switch
    {
        "billing" => new BillingDbContext(
            new DbContextOptionsBuilder<BillingDbContext>().UseNpgsql(FakeConnection).Options,
            new DomainEventToOutboxInterceptor()),
        "ledger" => new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(FakeConnection,
                o => o.MigrationsHistoryTable("__ef_migrations", "ledger")).Options),
        "payments" => new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(FakeConnection,
                o => o.MigrationsHistoryTable("__ef_migrations", "payments")).Options),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static IEnumerable<IReadOnlyProperty> AllProperties(IReadOnlyModel model) =>
        model.GetEntityTypes().SelectMany(e =>
            e.GetProperties().Concat(e.GetComplexProperties().SelectMany(c => c.ComplexType.GetProperties())));

    [Theory]
    [MemberData(nameof(Contexts))]
    public void EveryDecimalProperty_ShouldMapToNumeric18_2(string name)
    {
        using var db = Create(name);
        var model = db.GetService<IDesignTimeModel>().Model;

        var decimals = AllProperties(model)
            .Where(p => Nullable.GetUnderlyingType(p.ClrType) == typeof(decimal) || p.ClrType == typeof(decimal))
            .ToList();

        decimals.Should().NotBeEmpty($"{name} tem ao menos uma coluna monetaria");
        decimals.Should().AllSatisfy(p =>
        {
            p.GetPrecision().Should().Be(18, p.Name);
            p.GetScale().Should().Be(2, p.Name);
            ((IProperty)p).GetColumnType().Should().Be("numeric(18,2)", p.Name);
        });
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void NoFloatingPointColumns(string name)
    {
        using var db = Create(name);
        var model = db.GetService<IDesignTimeModel>().Model;

        AllProperties(model)
            .Where(p => p.ClrType == typeof(double) || p.ClrType == typeof(float)
                     || p.ClrType == typeof(double?) || p.ClrType == typeof(float?))
            .Select(p => p.Name)
            .Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Model_ShouldNotHavePendingChangesAgainstMigrations(string name)
    {
        using var db = Create(name);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "mudanca de mapeamento sem migration deixaria o banco divergente do modelo");
    }
}
