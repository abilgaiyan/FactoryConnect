namespace FactoryConnect.Abstractions;

public sealed record PlannedProductionWindow
{
    public required TimeOnly StartsAtLocal { get; init; }
    public required TimeOnly EndsAtLocal { get; init; }

    public bool IsOvernight => EndsAtLocal < StartsAtLocal;

    public void Validate()
    {
        if (StartsAtLocal == EndsAtLocal)
        {
            throw new ArgumentException(
                "Planned production window start and end times must be different.",
                nameof(EndsAtLocal));
        }
    }
}
