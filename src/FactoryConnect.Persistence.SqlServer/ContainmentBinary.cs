using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FactoryConnect.Persistence.SqlServer;

// Shared only by the new FCWE V1 / FCDS V2 formats. Frozen V1 codecs are untouched.
internal sealed class ContainmentBinaryWriter(int maximumLength) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly MemoryStream _stream = new();

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > maximumLength - _stream.Length)
            throw new InvalidDataException("Containment envelope exceeds its size limit.");
        _stream.Write(bytes);
    }

    public void U8(byte value) { Span<byte> bytes = stackalloc byte[1]; bytes[0] = value; Bytes(bytes); }
    public void U16(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); Bytes(bytes); }
    public void U32(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); Bytes(bytes); }
    public void U64(ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); Bytes(bytes); }
    public void I32(int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); Bytes(bytes); }
    public void I64(long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); Bytes(bytes); }
    public void Guid(Guid value) { Span<byte> bytes = stackalloc byte[16]; value.TryWriteBytes(bytes, bigEndian: true, out _); Bytes(bytes); }
    public void Timestamp(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new InvalidDataException("Containment timestamps must use UTC.");
        I64(value.Ticks);
    }
    public void String(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256) throw new InvalidDataException("Invalid containment string length.");
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > 768) throw new InvalidDataException("Invalid containment UTF-8 length.");
        Blob(bytes, 768);
    }
    public void OptionalString(string? value) { U8(value is null ? (byte)0 : (byte)1); if (value is not null) String(value); }
    public void Decimal(decimal value) => String(CanonicalDecimalTextV1Codec.Serialize(value));
    public void OptionalDecimal(decimal? value) { U8(value is null ? (byte)0 : (byte)1); if (value is not null) Decimal(value.Value); }
    public void Blob(ReadOnlySpan<byte> value, int maximum)
    {
        if (value.IsEmpty || value.Length > maximum) throw new InvalidDataException("Invalid containment blob length.");
        U32((uint)value.Length); Bytes(value);
    }
    public byte[] ToArray() => _stream.ToArray();
    public void Dispose() => _stream.Dispose();
}

internal sealed class ContainmentBinaryReader(ReadOnlyMemory<byte> encoded)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private int _offset;
    public int Remaining => encoded.Length - _offset;
    public ReadOnlyMemory<byte> Take(int length)
    {
        if (length < 0 || length > Remaining) throw new InvalidDataException("Truncated containment envelope.");
        var result = encoded.Slice(_offset, length); _offset += length; return result;
    }
    public void Require(ReadOnlySpan<byte> expected)
    {
        if (!Take(expected.Length).Span.SequenceEqual(expected)) throw new InvalidDataException("Invalid containment envelope magic.");
    }
    public void End() { if (Remaining != 0) throw new InvalidDataException("Trailing containment bytes."); }
    public byte U8() => Take(1).Span[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2).Span);
    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4).Span);
    public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8).Span);
    public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4).Span);
    public long I64() => BinaryPrimitives.ReadInt64BigEndian(Take(8).Span);
    public Guid Guid() => new(Take(16).Span, bigEndian: true);
    public DateTimeOffset Timestamp() => new(I64(), TimeSpan.Zero);
    public bool Present() => U8() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid containment nullable tag.") };
    public string String()
    {
        var value = StrictUtf8.GetString(Blob(768).Span);
        if (value.Length is 0 or > 256) throw new InvalidDataException("Invalid containment string length.");
        return value;
    }
    public string? OptionalString() => Present() ? String() : null;
    public decimal Decimal()
    {
        var text = String();
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            CanonicalDecimalTextV1Codec.Serialize(value) != text)
            throw new InvalidDataException("Noncanonical containment decimal.");
        return value;
    }
    public decimal? OptionalDecimal() => Present() ? Decimal() : null;
    public ReadOnlyMemory<byte> Blob(int maximum)
    {
        var length = U32();
        if (length == 0 || length > maximum || length > Remaining) throw new InvalidDataException("Invalid containment blob length.");
        return Take((int)length);
    }
    public int Count(int maximum, int minimumItemBytes)
    {
        var count = U32();
        if (count > maximum || count > Remaining / minimumItemBytes) throw new InvalidDataException("Invalid containment count.");
        return (int)count;
    }
}
