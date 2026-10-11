using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineActivityReportTests
{
    private static readonly MachineId Machine = new(Guid.Parse("de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed"));
    private static readonly ObservationStreamId Stream = new(Machine, "activity");
    private static readonly ObservationProcessorId Processor = new("machine-state-activity");
    private static readonly CurrentStateAuthorityBinding Binding = new(Machine, Stream, new("canonical-mapping"), Processor);
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static DurableMachineActivityPeriod Period(ulong position, MachineState state, DateTimeOffset start, DateTimeOffset end) =>
        new(Processor, new(position), Stream, 1, position, new(Machine, state, start, end));
    private static MachineActivityReport Report(params DurableMachineActivityPeriod[] periods) =>
        MachineActivityReportComposer.Compose(Binding, new(10), Start, Start.AddHours(1), Start.AddDays(1), periods);

    [Fact]
    public void EmptyReadDoesNotManufactureStateDuration()
    {
        var report = Report();
        Assert.Empty(report.Intervals);
        Assert.All(report.Totals, total => Assert.Equal(0L, total.DurationTicks));
        Assert.Equal(TimeSpan.FromHours(1).Ticks, report.UnrepresentedTicks);
    }

    [Fact]
    public void ClipsToSelectionAndPreservesExactOriginalFragment()
    {
        var report = Report(Period(1, MachineState.Running, Start.AddHours(-1), Start.AddTicks(1234567)));
        var interval = Assert.Single(report.Intervals);
        Assert.Equal(Start, interval.StartedAtUtc);
        Assert.Equal(Start.AddHours(-1), interval.SourceStartedAtUtc);
        Assert.Equal(1234567L, interval.DurationTicks);
        Assert.Equal(TimeSpan.FromHours(1).Ticks - 1234567, report.UnrepresentedTicks);
    }

    [Fact]
    public void RecordedUnknownIsDifferentFromUnrepresentedTime()
    {
        var report = Report(Period(1, MachineState.Unknown, Start, Start.AddMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, report.Totals.Single(t => t.State == "unknown").DurationTicks);
        Assert.Equal(TimeSpan.FromMinutes(50).Ticks, report.UnrepresentedTicks);
        Assert.Equal(0L, report.Totals.Single(t => t.State == "stopped").DurationTicks);
    }

    [Fact]
    public void OverlappingIntervalsDoNotProduceTotals()
    {
        Assert.Throws<InvalidDataException>(() => Report(
            Period(1, MachineState.Running, Start, Start.AddMinutes(30)),
            Period(2, MachineState.Stopped, Start.AddMinutes(20), Start.AddMinutes(40))));
    }

    [Fact]
    public void PostCheckpointAndNonAdvancingPositionsAreIntegrityFailures()
    {
        Assert.Throws<InvalidDataException>(() => Report(Period(11, MachineState.Running, Start, Start.AddMinutes(1))));
        Assert.Throws<InvalidDataException>(() => Report(
            Period(1, MachineState.Running, Start, Start.AddMinutes(1)),
            Period(1, MachineState.Running, Start.AddMinutes(1), Start.AddMinutes(2))));
    }

    [Fact]
    public void MissingCheckpointCannotAdmitStoredIntervals()
    {
        Assert.Throws<InvalidDataException>(() => MachineActivityReportComposer.Compose(Binding, null,
            Start, Start.AddHours(1), Start, [Period(1, MachineState.Running, Start, Start.AddMinutes(1))]));
    }
}
