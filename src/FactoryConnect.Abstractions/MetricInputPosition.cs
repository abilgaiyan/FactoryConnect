namespace FactoryConnect.Abstractions;

public sealed record MetricInputPosition :
    IComparable<MetricInputPosition>
{
    public MetricInputPosition(ulong value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Metric input position must be greater than zero.");
        }

        Value = value;
    }

    public ulong Value { get; }

    public int CompareTo(MetricInputPosition? other) =>
        other is null ? 1 : Value.CompareTo(other.Value);

    public static bool operator <(
        MetricInputPosition? left,
        MetricInputPosition? right) =>
        Compare(left, right) < 0;

    public static bool operator <=(
        MetricInputPosition? left,
        MetricInputPosition? right) =>
        Compare(left, right) <= 0;

    public static bool operator >(
        MetricInputPosition? left,
        MetricInputPosition? right) =>
        Compare(left, right) > 0;

    public static bool operator >=(
        MetricInputPosition? left,
        MetricInputPosition? right) =>
        Compare(left, right) >= 0;

    public override string ToString() => Value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    private static int Compare(
        MetricInputPosition? left,
        MetricInputPosition? right)
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
