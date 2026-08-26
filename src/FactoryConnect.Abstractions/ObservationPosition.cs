namespace FactoryConnect.Abstractions;

public readonly record struct ObservationPosition :
    IComparable<ObservationPosition>
{
    public ObservationPosition(ulong value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Observation position must be greater than zero.");
        }

        Value = value;
    }

    public ulong Value { get; }

    public int CompareTo(ObservationPosition other) =>
        Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}
