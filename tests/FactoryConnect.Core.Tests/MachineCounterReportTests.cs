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

    private static DurableMachineObservation EnumerationSample(ulong position, string? value, ulong instance = 1,
        ObservationQuality quality = ObservationQuality.Good, DateTimeOffset? time = null) =>
        new(new(position), Stream, instance, position, new MachineObservation
        {
            MachineId = Machine, Source = "mtconnect", Address = "part_count", Type = SignalType.Enumeration,
            Value = value, Quality = quality, Timestamp = time ?? Start.AddMinutes(position),
        });

    [Fact]
    public void EnumerationIntegerTextIsDiscoveredAndComparedWithoutChangingRawContent()
    {
        var report = Report(EnumerationSample(1, "00151", time: Start.AddTicks(-1)), EnumerationSample(2, "00152"));
        var row = Assert.Single(report.Samples);
        Assert.Equal("00152", row.RawValue);
        Assert.Equal("Enumeration", row.SignalType);
        Assert.Equal("1", row.ArithmeticDifference);
        Assert.Equal("1", row.PreviousPosition);
        Assert.DoesNotContain("NotUsableAsCounter", row.Flags);
        Assert.DoesNotContain("PreviousEvidenceUnusable", row.Flags);
        Assert.Contains("ContinuityNotEstablished", row.Flags);
        Assert.Contains("AttributionNotEstablished", row.Flags);
        Assert.Equal(new CounterSignal("mtconnect", "part_count"), Assert.Single(report.AvailableSignals));
    }

    [Fact]
    public void EnumerationDifferencesPreserveValuesBeyondDecimalAndJavascriptRanges()
    {
        const string before = "99999999999999999999999999999999999999999999999999";
        const string after = "100000000000000000000000000000000000000000000000001";
        var report = Report(EnumerationSample(1, before), EnumerationSample(2, after));
        Assert.Equal(before, report.Samples[0].RawValue);
        Assert.Equal(after, report.Samples[1].RawValue);
        Assert.Equal("2", report.Samples[1].ArithmeticDifference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNAVAILABLE")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("١")]
    public void InvalidEnumerationTokensRemainRawAndBreakTheComparisonChain(string token)
    {
        var report = Report(EnumerationSample(1, "151"), EnumerationSample(2, token), EnumerationSample(3, "153"));
        Assert.Equal(token, report.Samples[1].RawValue);
        Assert.Contains("NotUsableAsCounter", report.Samples[1].Flags);
        Assert.Contains("PreviousEvidenceUnusable", report.Samples[2].Flags);
        Assert.Null(report.Samples[1].ArithmeticDifference);
        Assert.Null(report.Samples[2].ArithmeticDifference);
        Assert.Single(report.AvailableSignals);
    }

    [Fact]
    public void EnumerationDecreaseAndInstanceChangeRemainSeparateFromArithmetic()
    {
        var report = Report(EnumerationSample(1, "300"), EnumerationSample(2, "0"), EnumerationSample(3, "1", 2));
        Assert.Contains("CounterDecreased", report.Samples[1].Flags);
        Assert.Contains("InstanceChanged", report.Samples[2].Flags);
        Assert.All(report.Samples, row => Assert.Null(row.ArithmeticDifference));
    }

    [Fact]
    public void EnumerationMissingAndPoorQualityValuesAreNotSkippedToBridgeEvidence()
    {
        var report = Report(EnumerationSample(1, "151"), EnumerationSample(2, null),
            EnumerationSample(3, "153"), EnumerationSample(4, "154", quality: ObservationQuality.Uncertain),
            EnumerationSample(5, "155"));
        Assert.Contains("MissingValue", report.Samples[1].Flags);
        Assert.Contains("NonGoodEvidence", report.Samples[3].Flags);
        Assert.Contains("PreviousEvidenceUnusable", report.Samples[2].Flags);
        Assert.Contains("PreviousEvidenceUnusable", report.Samples[4].Flags);
        Assert.All(report.Samples, row => Assert.Null(row.ArithmeticDifference));
    }

    [Fact]
    public void SignalTypeChangesAndNonIncreasingTimeSuppressEnumerationDifferences()
    {
        var report = Report(Sample(1, 151), EnumerationSample(2, "152"),
            EnumerationSample(3, "153", time: Start.AddMinutes(2)));
        Assert.Contains("SignalTypeChanged", report.Samples[1].Flags);
        Assert.Contains("NonIncreasingTimestamp", report.Samples[2].Flags);
        Assert.All(report.Samples, row => Assert.Null(row.ArithmeticDifference));
    }

    [Fact]
    public void TextSignalsAreNotImplicitlyParsedAsEnumerationCounters()
    {
        var source = EnumerationSample(1, "151");
        var text = new DurableMachineObservation(source.Position, source.StreamId, source.InstanceId, source.Sequence,
            source.Observation with { Type = SignalType.Text });
        var report = Report(text, EnumerationSample(2, "152"));
        Assert.Contains("NotUsableAsCounter", report.Samples[0].Flags);
        Assert.Null(report.Samples[1].ArithmeticDifference);
    }
}
