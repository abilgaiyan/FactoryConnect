using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class AssumedBreakAssessmentV1CodecTests
{
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);

    private static AssumedBreakAssessmentTarget Target(string? running = "RUN-1") =>
        new(new SiteId("GG-SITE"), Machine, new DateOnly(2026, 10, 16),
            Start, Start.AddHours(8), "SHIFT-1", "calendar-v1", "break-rule-v1",
            "DemoProvisional", "POLICY-1", "policy-v1", new DateOnly(2026, 10, 1),
            null, TimeSpan.FromMinutes(30), "Gajra-demo-instruction",
            new(new MetricAggregationProcessorId("metric-aggregation-v1"),
                MetricInputStreamId.ForMachine(Machine), new MetricInputPosition(534)),
            running, new string('A', 64));

    private static DurableAssumedBreakAssessment Assessment(string? running = "RUN-1", bool forecast = false) =>
        new(Target(running),
            new(TimeSpan.FromMinutes(480), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(450), forecast, true),
            running is null ? null : TimeSpan.Zero,
            forecast ? "BreakTimingNotEstablished" :
                running is null ? "RunningEvidenceNotEstablished" : "BreakAttributionNotEstablished", null);

    [Fact]
    public void CanonicalRoundTrip()
    {
        var value = Assessment();
        var bytes = AssumedBreakAssessmentV1Codec.Encode(value);
        var decoded = AssumedBreakAssessmentV1Codec.Decode(bytes, value.Identity);
        Assert.Equal(value, decoded);
        Assert.Equal(bytes, AssumedBreakAssessmentV1Codec.Encode(decoded));
    }

    [Fact]
    public void ChangedBindingsChangeIdentity()
    {
        var t = Target();
        var changed = new[]
        {
            t with { CalendarVersion = "v2" },
            t with { CalculationRuleVersion = "v2" },
            t with { AdmissionMode = "Approved" },
            t with { ExclusionEvidenceFingerprint = new string('B', 64) },
            t with { RunningEvidenceReference = null },
            t with { ScheduleId = "SHIFT-2" },
            t with { PolicyVersion = "v2" },
            t with { AggregationRevision = new(t.AggregationRevision.ProcessorId,
                t.AggregationRevision.StreamId, new MetricInputPosition(535)) },
        };
        foreach (var other in changed)
            Assert.NotEqual(AssumedBreakAssessmentV1Codec.Identity(t),
                AssumedBreakAssessmentV1Codec.Identity(other));
    }

    [Fact]
    public void MissingRunningAndEvidenceBackedZeroAreDistinct()
    {
        var missing = AssumedBreakAssessmentV1Codec.Decode(AssumedBreakAssessmentV1Codec.Encode(Assessment(null)));
        var zero = AssumedBreakAssessmentV1Codec.Decode(AssumedBreakAssessmentV1Codec.Encode(Assessment()));
        Assert.Null(missing.ObservedRunningDuration);
        Assert.Equal(TimeSpan.Zero, zero.ObservedRunningDuration);
        Assert.NotEqual(missing.Identity, zero.Identity);
    }

    [Fact]
    public void ForecastAndUnsupportedVersion()
    {
        var forecast = AssumedBreakAssessmentV1Codec.Decode(
            AssumedBreakAssessmentV1Codec.Encode(Assessment(forecast: true)));
        Assert.True(forecast.PlannedTime!.IsForecast);
        var bytes = AssumedBreakAssessmentV1Codec.Encode(Assessment());
        bytes[0] = 2;
        Assert.Throws<NotSupportedException>(() => AssumedBreakAssessmentV1Codec.Decode(bytes));
    }

    [Fact]
    public void InvalidPayloadAndMismatchedIdentity()
    {
        var value = Assessment();
        var bytes = AssumedBreakAssessmentV1Codec.Encode(value);
        Assert.Throws<InvalidDataException>(() => AssumedBreakAssessmentV1Codec.Decode(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => AssumedBreakAssessmentV1Codec.Decode(bytes,
            AssumedBreakAssessmentV1Codec.Identity(Target() with { CalendarVersion = "v2" })));
    }

}
