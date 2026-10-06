using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.HistoricalRecovery;
using Xunit;

namespace FactoryConnect.HistoricalRecovery.Tests;

public sealed class HistoricalRecoveryPreviewTests
{
    [Fact]
    public async Task JsonRecordPreservesExactPeriodAndRecursiveEvidenceIdentity()
    {
        var (target, capture) = Fixture();
        var record = await new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target);
        var json = RecoveryJson.Element(record);
        var period = json.GetProperty("Evaluation1")[0].GetProperty("Key").GetProperty("PeriodId");
        Assert.Equal("ProductionDay", period.GetProperty("PeriodType").GetString());
        Assert.Equal("2026-10-04", period.GetProperty("ProductionDayId").GetProperty("BusinessDate").GetString());
        var oee = json.GetProperty("Evaluation1").EnumerateArray().Single(value =>
            value.GetProperty("Key").GetProperty("DefinitionId").GetProperty("MetricKey").GetString() == "oee");
        Assert.Equal(3, oee.GetProperty("DependencyEvidence").GetArrayLength());
        Assert.Equal(2, oee.GetProperty("DependencyEvidence")[0].GetProperty("Evaluation").GetProperty("OperandEvidence").GetArrayLength());
    }

    [Fact]
    public async Task ReconstructsFiveMetricsAndRetainsRecursiveInsufficiencyEvidence()
    {
        var (target, capture) = Fixture();
        var reader = new FakeReader(capture);
        var record = await new HistoricalReconstruction(reader).RunAsync(target);
        Assert.Equal(2, reader.ReadCount);
        Assert.Equal(5, record.Evaluation1.Count);
        Assert.Equal("PASS", record.ExactOutcomeAndRecursiveEvidenceEquivalence);
        Assert.Equal("PASS", record.HistoricalReadRepeatEquivalence);
        Assert.Equal("UNESTABLISHED", record.DefinitionAuthority.ExactHistoricalExecutionSha);
        Assert.Contains("WithinIdentifiedCandidateSet", record.DefinitionAuthority.Applicability, StringComparison.Ordinal);
        Assert.Equal(0.5m, Find(record, "availability").Value);
        Assert.Equal(0.25m, Find(record, "utilization.elr").Value);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingReferenceTime, Find(record, "performance").ReasonCode);
        Assert.Equal("IdealProductionDuration", Find(record, "performance").ReasonOperandName);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, Find(record, "quality").ReasonCode);
        var oee = Find(record, "oee");
        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, oee.Status);
        Assert.Equal(3, oee.DependencyEvidence.Count);
        Assert.All(oee.DependencyEvidence, evidence => Assert.Equal(target.Revision, evidence.Evaluation.SourceRevision));
        Assert.Equal(4, record.SelectedPeriodFacts.Count);
        // stopped is in reconstruction evidence but never a metric operand.
        Assert.DoesNotContain(record.Capture.Snapshot.Components,
            component => component.SourceIdentity.ComponentKey == MetricInputFactKeys.StoppedDuration);
    }

    [Fact]
    public async Task NeighboringPeriodMembershipDoesNotAuthorizeNeighborEvaluation()
    {
        var (target, capture) = Fixture();
        var neighbor = new ProductionDayId(target.Day.SiteId, target.Day.BusinessDate.AddDays(1));
        capture = capture with { Revision = new MetricAggregationRevisionChange(target.Revision, [], [target.Day, neighbor]) };
        var record = await new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target);
        Assert.All(record.Evaluation1, result => Assert.Equal(target.Period, result.Key.PeriodId));
    }

    [Fact]
    public async Task MissingPeriodMembershipFailsWithoutRecord()
    {
        var (target, capture) = Fixture();
        capture = capture with { Revision = new MetricAggregationRevisionChange(target.Revision, [], []) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target));
    }

    [Fact]
    public async Task MissingContributionForPersistedFactFailsCompletenessGate()
    {
        var (target, capture) = Fixture();
        capture = capture with { PrefixMembership = capture.PrefixMembership.Skip(1).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target));
    }

    [Fact]
    public async Task IncorrectPositionBindingFails()
    {
        var (target, capture) = Fixture();
        var identities = capture.PrefixMembership.ToArray();
        identities[0] = identities[0] with { FactId = "different-fact" };
        capture = capture with { PrefixMembership = identities };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target));
    }

    [Fact]
    public async Task PendingTransitionFailsEvenWhenMetricOperandsAreOtherwisePresent()
    {
        var (target, capture) = Fixture();
        capture = capture with { Transition = new ProductionReferenceTimePublicationTransition(
            target.Revision.ProcessorId, target.Revision.Position, null, null,
            new ProductionReferenceTimeAuthorityRevision(0), null, false) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target));
    }

    [Fact]
    public async Task HistoricalSnapshotValueMismatchFailsReconciliation()
    {
        var (target, capture) = Fixture();
        capture = capture with { Snapshot = ChangeRunning(capture.Snapshot, 99m) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target));
    }

    [Fact]
    public async Task MissingPresentComponentFailsReconciliation()
    {
        var (target, capture) = Fixture();
        capture = capture with { Snapshot = new OperationalMetricComponentSnapshot(
            capture.Snapshot.EvaluationKey, target.Revision, capture.Snapshot.Components.Skip(1)) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target));
    }

    [Fact]
    public async Task SecondCaptureDriftFailsDespiteDeterministicEvaluation()
    {
        var (target, capture) = Fixture();
        var changed = capture with { Snapshot = ChangeRunning(capture.Snapshot, 99m) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HistoricalReconstruction(new FakeReader(capture, changed)).RunAsync(target));
    }

    [Fact]
    public async Task LiveAdvancementDoesNotChangeHistoricalFingerprint()
    {
        var (target, capture) = Fixture();
        var advanced = capture with { LiveAuthority = RecoveryJson.Element(new { Checkpoint = 9999 }) };
        var record = await new HistoricalReconstruction(new FakeReader(capture, advanced)).RunAsync(target);
        Assert.Equal("PASS", record.HistoricalReadRepeatEquivalence);
        Assert.Equal(9999, record.LiveAuthorityAfter.GetProperty("Checkpoint").GetInt32());
    }

    [Fact]
    public async Task CancelledReadDoesNotBecomeInsufficientEvidence()
    {
        var (target, capture) = Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target, cancellation.Token));
    }

    [Fact]
    public async Task PinnedAdapterRejectsChangedPeriod()
    {
        var (target, capture) = Fixture();
        var key = new OperationalMetricEvaluationKey(target.Revision.StreamId.MachineId,
            new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(target.Day.SiteId, target.Day.BusinessDate.AddDays(1))),
            BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new FrozenSnapshotReader(capture.Snapshot)
            .ReadAsync(new OperationalMetricComponentSnapshotRequest(key, target.Revision.ProcessorId, []), CancellationToken.None));
    }

    [Fact]
    public async Task PinnedAdapterFiltersComponentsForNormalEvaluatorPlan()
    {
        var (_, capture) = Fixture();
        var definition = BuiltInOperationalMetricDefinitions.All.Single(value => value.Id == BuiltInOperationalMetricDefinitions.AvailabilityId);
        var operands = definition.Operands.Select(operand => operand with
        {
            OperandName = ((OperationalMetricOperandSource.Component)operand.Source).ComponentKey,
        }).ToArray();
        var snapshot = await new FrozenSnapshotReader(capture.Snapshot).ReadAsync(
            new OperationalMetricComponentSnapshotRequest(capture.Snapshot.EvaluationKey, capture.Snapshot.Revision.ProcessorId, operands),
            CancellationToken.None);
        Assert.Equal(2, snapshot.Components.Count);
        Assert.Equal(capture.Snapshot.Revision, snapshot.Revision);
    }

    [Fact]
    public async Task ComparisonDetectsNestedEvidenceChangeWithoutScalarChange()
    {
        var (target, capture) = Fixture();
        var record = await new HistoricalReconstruction(new FakeReader(capture)).RunAsync(target);
        var changed = record.Evaluation1.ToArray();
        var index = Array.FindIndex(changed, value => value.Key.DefinitionId == BuiltInOperationalMetricDefinitions.OeeId);
        var dependencies = changed[index].DependencyEvidence.ToArray();
        dependencies[0] = dependencies[0] with
        {
            Evaluation = dependencies[0].Evaluation with { OperandEvidence = [] },
        };
        changed[index] = changed[index] with { DependencyEvidence = dependencies };
        Assert.False(HistoricalReconstruction.Equivalent(record.Evaluation1, changed));
    }

    private static EvaluationRecord Find(ReconstructionRecord record, string metric) =>
        record.Evaluation1.Single(value => value.Key.DefinitionId.MetricKey == metric);

    private static OperationalMetricComponentSnapshot ChangeRunning(OperationalMetricComponentSnapshot snapshot, decimal value) =>
        new(snapshot.EvaluationKey, snapshot.Revision, snapshot.Components.Select(component =>
            component.SourceIdentity.ComponentKey == MetricInputFactKeys.RunningDuration
                ? new OperationalMetricComponent(component.OperandName, component.SourceIdentity, component.Dimension,
                    new MetricAggregateValue(value, component.Aggregate.Unit, component.Aggregate.InputCount,
                        component.Aggregate.FirstInputTimestamp, component.Aggregate.LastInputTimestamp))
                : component));

    internal static (RecoveryTarget Target, HistoricalCapture Capture) Fixture()
    {
        var machine = new MachineId(Guid.Parse("de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed"));
        var site = new SiteId("CAMPUS-1");
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 4));
        var revision = new MetricAggregationCheckpoint(new MetricAggregationProcessorId($"metric-aggregation:{RecoveryTargets.Machine}"),
            new MetricInputStreamId(machine, "metric-inputs"), new MetricInputPosition(1211));
        var target = new RecoveryTarget(revision, day);
        var starts = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var shift = new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId("schedule"), new ShiftId("SHIFT-1"), starts, starts.AddHours(8));
        var specs = new (string Key, decimal Value)[]
        {
            (MetricInputFactKeys.RunningDuration, 100m),
            (MetricInputFactKeys.PlannedProductionDuration, 200m),
            (MetricInputFactKeys.ScheduledDuration, 400m),
            (MetricInputFactKeys.StoppedDuration, 300m),
        };
        var facts = specs.Select((spec, index) => new PositionedMetricInputFact(revision.StreamId,
            new MetricInputPosition((ulong)index + 1), new DurableMetricInputFact
            {
                Id = new MetricInputFactId($"fact-{index}"), Key = spec.Key, Value = spec.Value, Unit = MetricInputFactUnits.Seconds,
                StartsAtUtc = starts, EndsAtUtc = starts.AddMinutes(10), CompanyId = new CompanyId("company"),
                SiteId = site, MachineId = machine, ShiftId = shift.ShiftId, ShiftScheduleAssignmentId = shift.ShiftScheduleAssignmentId,
            }, shift, day)).ToArray();
        var aggregates = MetricInputContributionAggregator.Aggregate(revision.StreamId, facts);
        var components = aggregates.ProductionDayContributions.Where(value => value.Key.MetricInputKey != MetricInputFactKeys.StoppedDuration)
            .Select(value => new OperationalMetricComponent(OperationalMetricComponentKeyMapping.ComponentKey(value.Key.MetricInputKey),
                new OperationalMetricAggregateSourceIdentity(revision.ProcessorId, machine, target.Period, value.Key.MetricInputKey),
                MetricDimension.Duration, value.Value)).ToArray();
        var snapshot = new OperationalMetricComponentSnapshot(new OperationalMetricEvaluationKey(machine, target.Period,
            BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned), revision, components);
        var capture = new HistoricalCapture(new MetricAggregationRevisionChange(revision, [shift], [day]),
            facts.Select((value, index) => new ContributionIdentity(index + 1, value.Position.Value, value.Fact.Id.Value)).ToArray(),
            facts, snapshot, new ProductionReferenceTimePublicationTransition(revision.ProcessorId, revision.Position,
                new MetricInputPosition(1204), null, new ProductionReferenceTimeAuthorityRevision(0),
                new ProductionReferenceTimeAuthorityRevision(0), true),
            new ProductionQuantitySourceCut(revision, target.Period, []), [], null,
            RecoveryJson.Element(new { Checkpoint = 2512 }));
        return (target, capture);
    }

    internal sealed class FakeReader(HistoricalCapture first, HistoricalCapture? second = null) : IHistoricalRecoveryReader
    {
        public int ReadCount { get; private set; }

        public Task<HistoricalCapture> ReadAsync(RecoveryTarget target,
            IReadOnlyList<OperationalMetricOperandDefinition> operands, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(ReadCount == 1 ? first : second ?? first);
        }
    }
}
