using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineCounterReportTests
{
    private static readonly MachineId Machine = new(Guid.Parse("de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed"));
    private static readonly ObservationStreamId Stream = new(Machine, "mtconnect:M01");
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static DurableMachineObservation Sample(ulong position, decimal? value, ulong instance = 1,
        ObservationQuality quality = ObservationQuality.Good, string address = "part_count", DateTimeOffset? time = null) =>
        new(new(position), Stream, instance, position, new MachineObservation
        {
            MachineId = Machine, Source = "mtconnect", Address = address, Type = SignalType.Numeric,
            Value = value, Quality = quality, Timestamp = time ?? Start.AddMinutes(position),
        });
    private static MachineCounterReport Report(params DurableMachineObservation[] items) =>
        MachineCounterReportComposer.Compose(Stream, new(100), new(3), Start, Start.AddHours(1), Start.AddDays(1), "mtconnect", "part_count", items);

    [Fact]
    public void RawValuesRemainExactAndDifferenceDoesNotAdmitProduction()
    {
        var report = Report(Sample(1, 9007199254740993m), Sample(2, 9007199254740995m));
        Assert.Equal("9007199254740993", report.Samples[0].RawValue);
        Assert.Equal("2", report.Samples[1].ArithmeticDifference);
        Assert.Contains("ContinuityNotEstablished", report.Samples[1].Flags);
        Assert.Contains("AttributionNotEstablished", report.Samples[1].Flags);
    }

    [Fact]
    public void DecreaseAndInstanceBoundaryDoNotManufactureIncrements()
    {
        var report = Report(Sample(1, 300), Sample(2, 0), Sample(3, 5, 2));
        Assert.Null(report.Samples[1].ArithmeticDifference);
        Assert.Contains("CounterDecreased", report.Samples[1].Flags);
        Assert.Null(report.Samples[2].ArithmeticDifference);
        Assert.Contains("InstanceChanged", report.Samples[2].Flags);
    }

    [Fact]
    public void MissingAndNonGoodSamplesBreakArithmeticChain()
    {
        var report = Report(Sample(1, 10), Sample(2, null), Sample(3, 20), Sample(4, 30, quality: ObservationQuality.Bad), Sample(5, 40));
        Assert.Contains("MissingValue", report.Samples[1].Flags);
        Assert.Contains("PreviousEvidenceUnusable", report.Samples[2].Flags);
        Assert.Contains("NonGoodEvidence", report.Samples[3].Flags);
        Assert.All(report.Samples, s => Assert.Null(s.ArithmeticDifference));
    }

    [Fact]
    public void PredecessorOutsideSelectionIsRetainedWithoutMixingSignals()
    {
        var report = Report(Sample(1, 10, time: Start.AddTicks(-1)), Sample(2, 500, address: "speed"), Sample(3, 12));
        var row = Assert.Single(report.Samples);
        Assert.Equal("1", row.PreviousPosition); Assert.Equal("2", row.ArithmeticDifference);
        Assert.Equal(2, report.AvailableSignals.Count);
    }

    [Fact]
    public void NonIncreasingTimeAndFractionalValuesAreNotCounterDeltas()
    {
        var report = Report(Sample(1, 10), Sample(2, 11, time: Start), Sample(3, 11.5m));
        Assert.Contains("NonIncreasingTimestamp", report.Samples[1].Flags);
        Assert.Contains("NotUsableAsCounter", report.Samples[2].Flags);
        Assert.All(report.Samples, s => Assert.Null(s.ArithmeticDifference));
    }

    [Fact]
    public void ExactSelectionEmptyResultAndIntegrityRemainDistinct()
    {
        Assert.Empty(Report(Sample(1, 10, address: "PART_COUNT")).Samples);
        Assert.Throws<InvalidDataException>(() => Report(Sample(101, 10)));
        Assert.Throws<InvalidDataException>(() => Report(Sample(1, 10), Sample(1, 11)));
        Assert.Empty(Report(Sample(1, 10, time: Start.AddHours(1))).Samples);
    }
}
