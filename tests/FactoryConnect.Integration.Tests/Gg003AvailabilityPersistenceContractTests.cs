using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

// Contract probes only: no SQL fixture, host startup, database access or replay.
public sealed class Gg003AvailabilityPersistenceContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingUnavailableReasonRoundTripsThroughDependencyCodecAndHttp(bool shift)
    {
        var availability = CreateUnavailable(shift, OperationalMetricEvaluationReasonCode.ZeroDenominator);
        var oeeKey = new OperationalMetricEvaluationKey(availability.Key.MachineId, availability.Key.PeriodId,
            BuiltInOperationalMetricDefinitions.OeeId, availability.Key.ContextKey);
        var oee = new OperationalMetricProjection(availability.ProcessorId, oeeKey,
            OperationalMetricEvaluationStatus.Unavailable, null, OperationalMetricUnits.Ratio,
            OperationalMetricEvaluationReasonCode.DependencyUnavailable, "Availability", availability.SourceRevision,
            dependencyEvidence: [new OperationalMetricDependencyProjectionEvidence("Availability",
                BuiltInOperationalMetricDefinitions.AvailabilityId, availability)]);
        var bytes = OperationalMetricDependencySnapshotV1Codec.Encode(oee);
        var restored = OperationalMetricDependencySnapshotV1Decoder.Decode(bytes);
        Assert.True(OperationalMetricProjectionEquivalence.AreEquivalent(oee, restored));
        Assert.Equal(bytes, OperationalMetricDependencySnapshotV1Codec.Encode(restored));
        Assert.Null(restored.Value);
        Assert.Equal(OperationalMetricEvaluationReasonCode.DependencyUnavailable, restored.ReasonCode);
        var dependency = Assert.Single(restored.DependencyEvidence).Projection;
        Assert.Equal(2683.614157m, Assert.Single(dependency.OperandEvidence).Value);
        Assert.Equal(534UL, dependency.SourceRevision.Position.Value);
        var page = new ReportingPage<OperationalMetricQueryItem>(
            [OperationalMetricQueryItem.FromSummary(new OperationalMetricProjectionSummary(restored))], null);
        var response = OperationalMetricHttpMapper.ToResponse(page);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonSerializerOptions.Web));
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal("unavailable", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("value").ValueKind);
        Assert.Equal("DependencyUnavailable", item.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public void NewReasonCannotBeIntroducedByEnumOnlyChangeInFrozenV1Codec()
    {
        // A reason without its witness/policy is rejected before serialization.
        Assert.Throws<ArgumentException>(() => CreateUnavailable(false,
            OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction));
        // V1 continues to reject unsupported future enum slots.
        var candidate = CreateUnavailable(false, (OperationalMetricEvaluationReasonCode)7);
        var error = Assert.Throws<InvalidOperationException>(() => OperationalMetricDependencySnapshotV1Codec.Encode(candidate));
        Assert.Contains("Unsupported operational metric reason code", error.Message);
    }

    private static OperationalMetricProjection CreateUnavailable(bool shift, OperationalMetricEvaluationReasonCode reason)
    {
        var machine = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var site = new SiteId("rca-site");
        var start = new DateTimeOffset(2026, 10, 16, 0, 30, 0, TimeSpan.Zero);
        OperationalMetricPeriodId period = shift
            ? new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(site,
                new ShiftScheduleAssignmentId("rca-shift-1-schedule"), new ShiftId("SHIFT-1"), start, start.AddHours(8)))
            : new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(site, new DateOnly(2026, 10, 16)));
        var revision = new MetricAggregationCheckpoint(new MetricAggregationProcessorId($"metric-aggregation:{machine.Value}"),
            MetricInputStreamId.ForMachine(machine), new MetricInputPosition(534));
        return new OperationalMetricProjection(new OperationalMetricProjectionProcessorId($"operational-metrics:{machine.Value}:builtins-v1"),
            new OperationalMetricEvaluationKey(machine, period, BuiltInOperationalMetricDefinitions.AvailabilityId,
                OperationalMetricEvaluationContextKey.Unpartitioned), OperationalMetricEvaluationStatus.Unavailable,
            null, OperationalMetricUnits.Ratio, reason, "PlannedOperatingTime", revision,
            [new OperationalMetricComponentProjectionEvidence("ActualProductionTime",
                new OperationalMetricAggregateSourceIdentity(revision.ProcessorId, machine, period, MetricInputFactKeys.RunningDuration),
                revision, MetricDimension.Duration, 2683.614157m, MetricInputFactUnits.Seconds, 31,
                start.AddHours(2), start.AddHours(5))]);
    }
}
