namespace FactoryConnect.Abstractions;

public sealed record ObservationPosition :
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

    public int CompareTo(ObservationPosition? other) =>
        other is null ? 1 : Value.CompareTo(other.Value);

    public static bool operator <(
        ObservationPosition? left,
        ObservationPosition? right) =>
        Compare(left, right) < 0;

    public static bool operator <=(
        ObservationPosition? left,
        ObservationPosition? right) =>
        Compare(left, right) <= 0;

    public static bool operator >(
        ObservationPosition? left,
        ObservationPosition? right) =>
        Compare(left, right) > 0;

    public static bool operator >=(
        ObservationPosition? left,
        ObservationPosition? right) =>
        Compare(left, right) >= 0;

    public override string ToString() => Value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    private static int Compare(
        ObservationPosition? left,
        ObservationPosition? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        return right is null ? 1 : left.Value.CompareTo(right.Value);
    }
}
