namespace FactoryConnect.Abstractions;

public sealed record MetricAggregateValue
{
    public MetricAggregateValue(
        decimal value,
        string unit,
        long inputCount,
        DateTimeOffset firstInputTimestamp,
        DateTimeOffset lastInputTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        if (inputCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputCount),
                "Aggregate input count must be greater than zero.");
        }

        if (lastInputTimestamp < firstInputTimestamp)
        {
            throw new ArgumentException(
                "Last input timestamp must not precede the first input timestamp.",
                nameof(lastInputTimestamp));
        }

        Value = value;
        Unit = unit;
        InputCount = inputCount;
        FirstInputTimestamp = firstInputTimestamp;
        LastInputTimestamp = lastInputTimestamp;
    }

    public decimal Value { get; }

    public string Unit { get; }

    public long InputCount { get; }

    public DateTimeOffset FirstInputTimestamp { get; }

    public DateTimeOffset LastInputTimestamp { get; }
}
