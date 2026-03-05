namespace BillingLedger.SharedKernel.Primitives;

public readonly record struct EntityId(Guid Value)
{
    public static EntityId New() => new(Guid.NewGuid());
    public static EntityId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
