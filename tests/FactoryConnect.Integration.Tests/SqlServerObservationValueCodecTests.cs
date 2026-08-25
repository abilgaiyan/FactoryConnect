using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerObservationValueCodecTests
{
    [Fact]
    public void NumericDecimalSerializesInvariantly()
    {
        var value = SqlServerObservationValueCodec.Serialize(
            SignalType.Numeric,
            1234.50m);

        Assert.Equal("1234.50", value);
    }

    [Theory]
    [InlineData(SignalType.Enumeration, "ACTIVE")]
    [InlineData(SignalType.Text, "OVER TEMPERATURE")]
    [InlineData(SignalType.Text, "A ")]
    public void StringValuesPreserveExactText(
        SignalType type,
        string input)
    {
        var value = SqlServerObservationValueCodec.Serialize(type, input);

        Assert.Equal(input, value);
    }

    [Theory]
    [InlineData(SignalType.Numeric)]
    [InlineData(SignalType.Enumeration)]
    [InlineData(SignalType.Text)]
    public void SupportedSignalTypesPreserveNull(
        SignalType type)
    {
        var value = SqlServerObservationValueCodec.Serialize(type, null);

        Assert.Null(value);
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
    public void UnsupportedSignalTypeIsRejected(
        SignalType type)
    {
        Assert.Throws<InvalidOperationException>(
            () => SqlServerObservationValueCodec.Serialize(type, null));
    }

    [Fact]
    public void NumericEquivalenceUsesDecimalSemantics()
    {
        Assert.True(
            SqlServerObservationValueCodec.AreEquivalent(
                SignalType.Numeric,
                1.0m,
                1.00m));
    }

    [Fact]
    public void StringEquivalenceUsesOrdinalSemantics()
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
