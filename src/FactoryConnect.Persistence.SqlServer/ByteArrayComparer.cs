namespace FactoryConnect.Persistence.SqlServer;

internal sealed class ByteArrayComparer : IComparer<byte[]>
{
    public static ByteArrayComparer Instance { get; } = new();

    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }

        return x.AsSpan().SequenceCompareTo(y);
    }
}
