using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed partial class Gg003AvailabilityContainmentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAExactHistoricalWitnessClassifies534AndPreservesOeeEvidence(bool shift)
    {
        var store = await HistoricalStore(LoadInputs());
        var key = AvailabilityKey(shift);
        var snapshot = await store.ReadAtRevisionAsync(StageARequest(key), Revision, CancellationToken.None);
        var witness = Assert.IsType<AvailabilityScopeConflictEvidenceV1>(snapshot.AvailabilityScopeConflictEvidence);
        Assert.Equal(380UL, witness.Contribution.Position.Value);
        Assert.Equal(new MetricInputFactId("rca-fact-380"), witness.Contribution.Fact.Id);
        Assert.Equal(new ProductionTimeEligibilityIntervalId("rca-eligibility-380"), witness.Contribution.Fact.SourceEligibilityIntervalId);
        Assert.Equal(new ContextualizedActivityIntervalId("rca-activity-380"), witness.Contribution.Fact.SourceContextualizedActivityIntervalId);
        Assert.Equal(259.637610m, witness.Contribution.Fact.Value);
        Assert.Equal(TimeSpan.FromTicks(2596376100), witness.Contribution.Fact.EndsAtUtc - witness.Contribution.Fact.StartsAtUtc);
        Assert.Equal(Revision, witness.SourceRevision);
        Assert.Equal(AvailabilityContainmentV1.PlannedProductionOperandContractId, witness.PlannedProductionOperandContractId);

        var evaluator = StageAEvaluator(new PinnedReader(store));
        var availability = await evaluator.EvaluateAsync(key, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, availability.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, availability.ReasonCode);
        Assert.Null(availability.Value);
        Assert.Equal(witness, availability.AvailabilityScopeConflictEvidence);
        Assert.Equal(2683.614157m, availability.OperandEvidence[0].Value);
        Assert.Equal(2452.013355m, availability.OperandEvidence[1].Value);
        Assert.Equal(31, availability.OperandEvidence[0].InputCount);
        Assert.Equal(3, availability.OperandEvidence[1].InputCount);

        var oeeKey = new OperationalMetricEvaluationKey(Machine, key.PeriodId,
            BuiltInOperationalMetricDefinitions.OeeId, key.ContextKey);
        var oee = await evaluator.EvaluateAsync(oeeKey, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationReasonCode.DependencyUnavailable, oee.ReasonCode);
        Assert.Null(oee.Value);
        Assert.Equal(witness, oee.DependencyEvidence.Single(item => item.OperandName == "Availability").Evaluation.AvailabilityScopeConflictEvidence);
        var factory = new OperationalMetricProjectionFactory(Catalog(), AvailabilityContainmentV1.ProjectionProcessorId(Machine));
        var projection = factory.Create(oee);
        Assert.Equal(witness, projection.DependencyEvidence.Single(item => item.OperandName == "Availability").Projection.AvailabilityScopeConflictEvidence);
        Assert.Equal(AvailabilityContainmentV1.PolicyId, projection.EvaluationPolicyId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageASame109PercentWithoutWitnessStillThrows(bool shift)
    {
        var store = await HistoricalStore(LoadInputs());
        var evaluator = StageAEvaluator(new PinnedReader(store, omitWitness: true));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await evaluator.EvaluateAsync(AvailabilityKey(shift), CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StageA75PercentDependsOnPositiveContributionEvidence(bool shift, bool plannedRunning)
    {
        var store = await HistoricalStore(Synthetic75(plannedRunning));
        var evaluation = await StageAEvaluator(new PinnedReader(store)).EvaluateAsync(AvailabilityKey(shift), CancellationToken.None);
        if (plannedRunning)
        {
            Assert.Equal(OperationalMetricEvaluationStatus.Calculated, evaluation.Status);
            Assert.Equal(0.75m, evaluation.Value);
            Assert.Null(evaluation.AvailabilityScopeConflictEvidence);
        }
        else
        {
            Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, evaluation.ReasonCode);
            Assert.Null(evaluation.Value);
            Assert.NotNull(evaluation.AvailabilityScopeConflictEvidence);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageA75PercentWithoutWitnessStillCalculatesForIdenticalOperandFacts(bool shift)
    {
        var store = await HistoricalStore(Synthetic75(plannedRunning: false));
        var result = await StageAEvaluator(new PinnedReader(store, omitWitness: true))
            .EvaluateAsync(AvailabilityKey(shift), CancellationToken.None);
        Assert.Equal(0.75m, result.Value);
        Assert.Null(result.AvailabilityScopeConflictEvidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAMissingAndZeroDenominatorRetainPrecedence(bool shift)
    {
        // Explicit aggregate-only probes, separate from historical factory evidence.
        var fixture = Create(shift, LoadInputs());
        var evaluator = StageAEvaluator(fixture.Reader);
        fixture.Reader.Override(MetricInputKeys.PlannedOperatingTime, 0m);
        var zero = await evaluator.EvaluateAsync(fixture.Key, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationReasonCode.ZeroDenominator, zero.ReasonCode);
        fixture.Reader.Remove(MetricInputKeys.PlannedOperatingTime);
        var missing = await evaluator.EvaluateAsync(fixture.Key, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, missing.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, missing.ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAHistorical534ExcludesLaterContributionsAndSelectsLowestPosition(bool shift)
    {
        var inputs = LoadInputs();
        var store = await HistoricalStore(inputs.Reverse().ToArray());
        var extra = new PositionedMetricInputFact(Stream, new MetricInputPosition(600),
            inputs[0].Fact with { Id = new MetricInputFactId("later-600") }, Shift, Day);
        var later = new MetricAggregationCheckpoint(Processor, Stream, extra.Position);
        await store.CommitAsync(new MetricAggregationCommit(Processor, Revision, later, [extra]), CancellationToken.None);
        var snapshot = await store.ReadAtRevisionAsync(StageARequest(AvailabilityKey(shift)), Revision, CancellationToken.None);
        var witness = Assert.IsType<AvailabilityScopeConflictEvidenceV1>(snapshot.AvailabilityScopeConflictEvidence);
        Assert.Equal(380UL, witness.Contribution.Position.Value);
        Assert.Equal(31, witness.Numerator.Aggregate.InputCount);
        Assert.Equal(2683.614157m, witness.Numerator.Aggregate.Value);
        Assert.Equal(Revision, snapshot.Revision);
    }

    [Theory]
    [InlineData("late")]
    [InlineData("stream")]
    [InlineData("negative")]
    [InlineData("missing-eligibility")]
    [InlineData("assignment")]
    [InlineData("duration")]
    [InlineData("membership")]
    [InlineData("denominator-eligibility")]
    public async Task StageAInvalidOrUnincorporatedEvidenceIsRejected(string fault)
    {
        var inputs = LoadInputs();
        var store = await HistoricalStore(inputs);
        var request = StageARequest(AvailabilityKey(true));
        var snapshot = await store.ReadAtRevisionAsync(request, Revision, CancellationToken.None);
        var first = inputs[0];
        inputs[0] = fault switch
        {
            "late" => new PositionedMetricInputFact(Stream, new MetricInputPosition(600), first.Fact, Shift, Day),
            "stream" => new PositionedMetricInputFact(new MetricInputStreamId(Machine, "foreign-stream"), first.Position, first.Fact, Shift, Day),
            "negative" => new PositionedMetricInputFact(Stream, first.Position, first.Fact with { Value = -first.Fact.Value }, Shift, Day),
            "missing-eligibility" => new PositionedMetricInputFact(Stream, first.Position, first.Fact with { IsPlannedProductionTime = null }, Shift, Day),
            "assignment" => new PositionedMetricInputFact(Stream, first.Position, first.Fact with { PlannedProductionScheduleAssignmentId = new PlannedProductionScheduleAssignmentId("foreign") }, Shift, Day),
            "duration" => new PositionedMetricInputFact(Stream, first.Position, first.Fact with { EndsAtUtc = first.Fact.EndsAtUtc.AddSeconds(1) }, Shift, Day),
            _ => first,
        };
        if (fault == "membership") inputs = inputs.Skip(1).ToArray();
        if (fault == "denominator-eligibility")
        {
            var planned = inputs[^1];
            inputs[^1] = new PositionedMetricInputFact(Stream, planned.Position,
                planned.Fact with { IsPlannedProductionTime = false, PlannedProductionScheduleAssignmentId = null }, Shift, Day);
        }
        Assert.Throws<InvalidDataException>(() => AvailabilityScopeConflictEvidenceBuilder.Build(request, Revision, inputs, snapshot.Components));
    }

    [Fact]
    public async Task StageAWitnessCannotBeReboundToForeignRevisionPeriodOrChangedAggregate()
    {
        var store = await HistoricalStore(LoadInputs());
        var key = AvailabilityKey(true);
        var snapshot = await store.ReadAtRevisionAsync(StageARequest(key), Revision, CancellationToken.None);
        var witness = snapshot.AvailabilityScopeConflictEvidence;
        Assert.Throws<ArgumentException>(() => new OperationalMetricComponentSnapshot(key,
            new MetricAggregationCheckpoint(Processor, Stream, new MetricInputPosition(600)), snapshot.Components, witness));
        var foreign = AvailabilityKey(false);
        var foreignSnapshot = await store.ReadAtRevisionAsync(StageARequest(foreign), Revision, CancellationToken.None);
        Assert.Throws<ArgumentException>(() => new OperationalMetricComponentSnapshot(foreign, Revision, foreignSnapshot.Components, witness));
        var changed = snapshot.Components.Select(component => new OperationalMetricComponent(component.OperandName,
            component.SourceIdentity, component.Dimension, new MetricAggregateValue(component.Aggregate.Value + 1,
                component.Aggregate.Unit, component.Aggregate.InputCount, component.Aggregate.FirstInputTimestamp, component.Aggregate.LastInputTimestamp))).ToArray();
        Assert.Throws<ArgumentException>(() => new OperationalMetricComponentSnapshot(key, Revision, changed, witness));
    }

    [Fact]
    public async Task StageAUnrelatedOutOfPlanRunningCannotBecomeNumeratorWitness()
    {
        var inputs = Synthetic75(plannedRunning: true);
        var foreignOccurrence = new ShiftOccurrenceId(Site, new ShiftScheduleAssignmentId("other-schedule"), Shift.ShiftId, Shift.StartsAtUtc, Shift.EndsAtUtc);
        var extra = new PositionedMetricInputFact(Stream, new MetricInputPosition(500), inputs[0].Fact with
        {
            Id = new MetricInputFactId("other-period-running"), ShiftScheduleAssignmentId = foreignOccurrence.ShiftScheduleAssignmentId,
            IsPlannedProductionTime = false, PlannedProductionScheduleAssignmentId = null,
        }, foreignOccurrence, Day);
        var store = await HistoricalStore([.. inputs, extra]);
        var result = await StageAEvaluator(new PinnedReader(store)).EvaluateAsync(AvailabilityKey(true), CancellationToken.None);
        Assert.Equal(0.75m, result.Value);
        Assert.Null(result.AvailabilityScopeConflictEvidence);
    }

    [Fact]
    public async Task StageALegacyAndNewProcessorCoexistButPublicationIsHeld()
    {
        var store = await HistoricalStore(Synthetic75(plannedRunning: false));
        var key = AvailabilityKey(true);
        var reader = new PinnedReader(store);
        var legacy = await new OperationalMetricEvaluator(Catalog(), reader, Processor).EvaluateAsync(key, CancellationToken.None);
        var contained = await StageAEvaluator(reader).EvaluateAsync(key, CancellationToken.None);
        Assert.Equal(0.75m, legacy.Value);
        Assert.Null(legacy.EvaluationPolicyId);
        var oldId = new OperationalMetricProjectionProcessorId($"operational-metrics:{Machine.Value}:builtins-v1");
        var oldFactory = new OperationalMetricProjectionFactory(Catalog(), oldId);
        Assert.Equal(0.75m, oldFactory.Create(legacy).Value);
        Assert.Throws<ArgumentException>(() => oldFactory.Create(contained));
        Assert.Throws<ArgumentException>(() => new OperationalMetricProjectionFactory(Catalog(),
            AvailabilityContainmentV1.ProjectionProcessorId(Machine)).Create(legacy));
        var processor = AvailabilityContainmentV1.ProjectionProcessorId(Machine);
        Assert.NotEqual(oldId, processor);
        var projection = new OperationalMetricProjectionFactory(Catalog(), processor).Create(contained);
        var checkpoint = new OperationalMetricProjectionCheckpoint(processor, Revision,
            new OperationalMetricProjectionBatchManifest([key]));
        Assert.Throws<NotSupportedException>(() => new OperationalMetricProjectionCommit(processor, null, checkpoint, [projection]));
    }

    private static OperationalMetricDefinitionCatalog Catalog() => new(BuiltInOperationalMetricDefinitions.All);
    private static OperationalMetricEvaluator StageAEvaluator(IOperationalMetricComponentSnapshotReader reader) =>
        new(Catalog(), reader, Processor, useAvailabilityContainmentV1: true);
    private static OperationalMetricEvaluationKey AvailabilityKey(bool shift) => new(Machine,
        shift ? new OperationalMetricPeriodId.Shift(Shift) : new OperationalMetricPeriodId.ProductionDay(Day),
        BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned);
    private static OperationalMetricComponentSnapshotRequest StageARequest(OperationalMetricEvaluationKey key) =>
        new(key, Processor, BuiltInOperationalMetricDefinitions.All.Single(definition => definition.Id == key.DefinitionId).Operands,
            includeAvailabilityScopeConflictEvidence: true);
    private static async Task<InMemoryMetricAggregationStore> HistoricalStore(PositionedMetricInputFact[] inputs)
    {
        var store = new InMemoryMetricAggregationStore();
        await store.CommitAsync(new MetricAggregationCommit(Processor, null, Revision, inputs), CancellationToken.None);
        return store;
    }
    private static PositionedMetricInputFact[] Synthetic75(bool plannedRunning)
    {
        var original = LoadInputs();
        var running = original[0].Fact with
        {
            Value = 75m, EndsAtUtc = original[0].Fact.StartsAtUtc.AddSeconds(75),
            IsPlannedProductionTime = plannedRunning,
            PlannedProductionScheduleAssignmentId = plannedRunning ? new PlannedProductionScheduleAssignmentId("synthetic-plan") : null,
        };
        var planned = original[^1].Fact with { Value = 100m, EndsAtUtc = original[^1].Fact.StartsAtUtc.AddSeconds(100) };
        return [new PositionedMetricInputFact(Stream, original[0].Position, running, Shift, Day),
            new PositionedMetricInputFact(Stream, original[^1].Position, planned, Shift, Day)];
    }
    private sealed class PinnedReader(InMemoryMetricAggregationStore store, bool omitWitness = false) : IOperationalMetricComponentSnapshotReader
    {
        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(OperationalMetricComponentSnapshotRequest request, CancellationToken cancellationToken) =>
            store.ReadAtRevisionAsync(new OperationalMetricComponentSnapshotRequest(request.EvaluationKey, request.ProcessorId,
                request.Operands, request.IncludeAvailabilityScopeConflictEvidence && !omitWitness), Revision, cancellationToken);
    }
}
