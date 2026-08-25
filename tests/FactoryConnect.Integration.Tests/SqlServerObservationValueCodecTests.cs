using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerObservationValueCodecTests
{
    [Fact]
    public void NumericDecimalSerializesCanonically()
    {
        Assert.Equal(
            "1234.5",
            SqlServerObservationValueCodec.Serialize(
                SignalType.Numeric,
                1234.50m));
        Assert.Equal(
            "1",
            SqlServerObservationValueCodec.Serialize(
                SignalType.Numeric,
                1.00m));
    }

    [Fact]
    public void NumericValuesRoundTripAcrossDecimalBoundaries()
    {
        decimal[] values =
        [
            decimal.MinValue,
            decimal.MaxValue,
            0m,
            -1234.567890123456789m,
            0.0000000000000000000000000001m,
        ];

        foreach (var value in values)
        {
            var persisted = SqlServerObservationValueCodec.Serialize(
                SignalType.Numeric,
                value);
            var restored = SqlServerObservationValueCodec.Deserialize(
                SignalType.Numeric,
                persisted);

            Assert.Equal(value, Assert.IsType<decimal>(restored));
        }
    }

    [Theory]
    [InlineData(SignalType.Enumeration, "")]
    [InlineData(SignalType.Enumeration, "ACTIVE")]
    [InlineData(SignalType.Text, "A ")]
    [InlineData(SignalType.Text, "A\0B")]
    [InlineData(SignalType.Text, "é")]
    [InlineData(SignalType.Text, "e\u0301")]
    [InlineData(SignalType.Text, "😀")]
    public void StringValuesRoundTripExactly(
        SignalType type,
        string input)
    {
        var persisted = SqlServerObservationValueCodec.Serialize(type, input);
        var restored = SqlServerObservationValueCodec.Deserialize(
            type,
            persisted);

        Assert.Equal(input, Assert.IsType<string>(restored));
    }

    [Theory]
    [InlineData(SignalType.Numeric)]
    [InlineData(SignalType.Enumeration)]
    [InlineData(SignalType.Text)]
    public void SupportedSignalTypesRoundTripNull(
        SignalType type)
    {
        var persisted = SqlServerObservationValueCodec.Serialize(type, null);
        var restored = SqlServerObservationValueCodec.Deserialize(
            type,
            persisted);

        Assert.Null(persisted);
        Assert.Null(restored);
    }

    [Fact]
    public void InvalidPersistedNumericTextIsRejected()
    {
        Assert.Throws<InvalidDataException>(
            () => SqlServerObservationValueCodec.Deserialize(
                SignalType.Numeric,
                "not-a-number"));
    }

    [Theory]
    [InlineData(SignalType.Numeric, 1)]
    [InlineData(SignalType.Numeric, 1.0)]
    [InlineData(SignalType.Numeric, "1")]
    [InlineData(SignalType.Enumeration, 1)]
    [InlineData(SignalType.Text, 1)]
    public void UnsupportedClrValueTypeIsRejected(
        SignalType type,
        object value)
    {
        Assert.Throws<InvalidOperationException>(
            () => SqlServerObservationValueCodec.Serialize(type, value));
    }

    [Theory]
    [InlineData(SignalType.Digital)]
    [InlineData(SignalType.Analog)]
    [InlineData(SignalType.Counter)]
    [InlineData(SignalType.WholeNumber)]
    [InlineData(SignalType.Timestamp)]
    public void UnsupportedSignalTypeIsRejectedForSerialization(
        SignalType type)
    {
        Assert.Throws<InvalidOperationException>(
            () => SqlServerObservationValueCodec.Serialize(type, null));
    }

    [Theory]
    [InlineData(SignalType.Digital)]
    [InlineData(SignalType.Analog)]
    [InlineData(SignalType.Counter)]
    [InlineData(SignalType.WholeNumber)]
    [InlineData(SignalType.Timestamp)]
    public void UnsupportedSignalTypeIsRejectedForDeserialization(
        SignalType type)
    {
        Assert.Throws<InvalidOperationException>(
            () => SqlServerObservationValueCodec.Deserialize(type, null));
    }

    [Fact]
    public void NumericEquivalenceUsesCanonicalRepresentation()
    {
        Assert.Equal(
            SqlServerObservationValueCodec.Serialize(
                SignalType.Numeric,
                1.0m),
            SqlServerObservationValueCodec.Serialize(
                SignalType.Numeric,
                1.00m));
        Assert.True(
            SqlServerObservationValueCodec.AreEquivalent(
                SignalType.Numeric,
                1.0m,
                1.00m));
        Assert.False(
            SqlServerObservationValueCodec.AreEquivalent(
                SignalType.Numeric,
                1m,
                2m));
    }

    [Fact]
    public void StringEquivalenceUsesCanonicalOrdinalRepresentation()
    {
        Assert.True(
            SqlServerObservationValueCodec.AreEquivalent(
                SignalType.Enumeration,
                "ACTIVE",
                "ACTIVE"));
        Assert.False(
            SqlServerObservationValueCodec.AreEquivalent(
                SignalType.Enumeration,
                "ACTIVE",
                "active"));
        Assert.False(
            SqlServerObservationValueCodec.AreEquivalent(
                SignalType.Text,
                "A",
                "A "));
    }

    [Fact]
    public void ObservationEquivalenceUsesCurrentRecordSemantics()
    {
        var machineId = MachineId.New();
        var leftTimestamp = new DateTimeOffset(
            2026,
            8,
            25,
            12,
            0,
            0,
            TimeSpan.Zero);
        var rightTimestamp = leftTimestamp.ToOffset(
            TimeSpan.FromHours(5.5));

        var left = CreateObservation(
            machineId,
            "mtconnect",
            "load",
            SignalType.Numeric,
            42.50m,
            leftTimestamp);
        var right = CreateObservation(
            machineId,
            "mtconnect",
            "load",
            SignalType.Numeric,
            42.500m,
            rightTimestamp);

        Assert.True(
            SqlServerObservationEquivalence.AreEquivalent(left, right));
    }

    [Fact]
    public void ObservationEquivalencePreservesOrdinalSourceAndAddress()
    {
        var machineId = MachineId.New();
        var timestamp = DateTimeOffset.UtcNow;
        var baseline = CreateObservation(
            machineId,
            "mtconnect",
            "exec",
            SignalType.Enumeration,
            "ACTIVE",
            timestamp);
        var changedSource = baseline with { Source = "MTCONNECT" };
        var changedAddress = baseline with { Address = "EXEC" };

        Assert.False(
            SqlServerObservationEquivalence.AreEquivalent(
                baseline,
                changedSource));
        Assert.False(
            SqlServerObservationEquivalence.AreEquivalent(
                baseline,
                changedAddress));
    }

    private static MachineObservation CreateObservation(
        MachineId machineId,
        string source,
        string address,
        SignalType type,
        object? value,
        DateTimeOffset timestamp) =>
        new()
        {
            MachineId = machineId,
            Source = source,
            Address = address,
            Type = type,
            Value = value,
            Quality = ObservationQuality.Good,
            Timestamp = timestamp,
        };
}
