using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerMetricAggregateKeyCodecTests
{
    [Fact]
    public void ShiftEncodingIsDeterministicAndUsesAllIdentityConstituents()
    {
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var start = new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var baseline = CreateShiftKey(machineId, "SITE-A", "SCHEDULE-A", "SHIFT-A", start, "running");

        var encoded = SqlServerMetricAggregateKeyCodec.Encode(baseline);

        Assert.Equal(encoded, SqlServerMetricAggregateKeyCodec.Encode(baseline));
        AssertDistinct(encoded, CreateShiftKey(machineId, "SITE-B", "SCHEDULE-A", "SHIFT-A", start, "running"));
        AssertDistinct(encoded, CreateShiftKey(machineId, "SITE-A", "SCHEDULE-B", "SHIFT-A", start, "running"));
        AssertDistinct(encoded, CreateShiftKey(machineId, "SITE-A", "SCHEDULE-A", "SHIFT-B", start, "running"));
        AssertDistinct(encoded, CreateShiftKey(machineId, "SITE-A", "SCHEDULE-A", "SHIFT-A", start.AddHours(1), "running"));
        AssertDistinct(encoded, CreateShiftKey(machineId, "SITE-A", "SCHEDULE-A", "SHIFT-A", start, "Running"));
    }

    [Fact]
    public void ProductionDayEncodingDistinguishesSiteDateMetricAndMachine()
    {
        var machineId = new MachineId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var baseline = CreateDayKey(machineId, "SITE-A", new DateOnly(2026, 8, 27), "running");
        var encoded = SqlServerMetricAggregateKeyCodec.Encode(baseline);

        AssertDistinct(encoded, CreateDayKey(machineId, "SITE-B", new DateOnly(2026, 8, 27), "running"));
        AssertDistinct(encoded, CreateDayKey(machineId, "SITE-A", new DateOnly(2026, 8, 28), "running"));
        AssertDistinct(encoded, CreateDayKey(machineId, "SITE-A", new DateOnly(2026, 8, 27), "Running"));
        AssertDistinct(
            encoded,
            CreateDayKey(
                new MachineId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                "SITE-A",
                new DateOnly(2026, 8, 27),
                "running"));
    }

    [Fact]
    public void LengthPrefixedStringsKeepConstituentBoundariesDistinct()
    {
        var machineId = new MachineId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var date = new DateOnly(2026, 8, 27);
        var first = CreateDayKey(machineId, "AB", date, "C");
        var second = CreateDayKey(machineId, "A", date, "BC");

        AssertDistinct(
            SqlServerMetricAggregateKeyCodec.Encode(first),
            second);
    }

    [Fact]
    public void HashIsDeterministicForCanonicalKey()
    {
        var key = CreateDayKey(
            new MachineId(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            "SITE-A",
            new DateOnly(2026, 8, 27),
            "running");
        var canonical = SqlServerMetricAggregateKeyCodec.Encode(key);

        Assert.Equal(
            SqlServerMetricAggregateKeyCodec.Hash(canonical),
            SqlServerMetricAggregateKeyCodec.Hash(canonical));
    }

    private static ShiftMetricAggregateKey CreateShiftKey(
        MachineId machineId,
        string site,
        string schedule,
        string shift,
        DateTimeOffset start,
        string metricKey) =>
        new(
            machineId,
            new ShiftOccurrenceId(
                new SiteId(site),
                new ShiftScheduleAssignmentId(schedule),
                new ShiftId(shift),
                start,
                start.AddHours(8)),
            metricKey);

    private static ProductionDayMetricAggregateKey CreateDayKey(
        MachineId machineId,
        string site,
        DateOnly day,
        string metricKey) =>
        new(
            machineId,
            new ProductionDayId(new SiteId(site), day),
            metricKey);

    private static void AssertDistinct(
        byte[] baseline,
        ShiftMetricAggregateKey other) =>
        Assert.False(
            baseline.SequenceEqual(SqlServerMetricAggregateKeyCodec.Encode(other)));

    private static void AssertDistinct(
        byte[] baseline,
        ProductionDayMetricAggregateKey other) =>
        Assert.False(
            baseline.SequenceEqual(SqlServerMetricAggregateKeyCodec.Encode(other)));
}
