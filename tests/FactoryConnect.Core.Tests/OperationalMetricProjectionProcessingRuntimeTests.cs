using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricProjectionProcessingRuntimeTests
{
    [Fact]
    public async Task NewerCoherentBatchPublishesProjectionAndCheckpoint()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(fixture, 40, 1m / 3m));

        var count = await fixture.Runtime.RunCycleAsync();

        Assert.Equal(1, count);
        var checkpoint = await fixture.Store.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.SourceStreamId,
            CancellationToken.None);
        Assert.Equal((ulong)40, checkpoint!.SourceRevision.Position.Value);
        Assert.Single(checkpoint.BatchManifest.ProjectionKeys);

        var stored = await fixture.Store.ReadProjectionAsync(
            fixture.ProjectionProcessorId,
            EvaluationKey(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            CancellationToken.None);
        Assert.Equal(0.33333333m, stored!.Value);
    }

    [Fact]
    public async Task ExactRevisionReplayWithIndependentlyConstructedEvidenceIsStructuralNoOp()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(BatchWithEvidence(fixture, 40, 0.5m));
        Assert.Equal(1, await fixture.Runtime.RunCycleAsync());

        fixture.Source.Enqueue(BatchWithEvidence(fixture, 40, 0.5m));
        Assert.Equal(0, await fixture.Runtime.RunCycleAsync());

        Assert.Equal((ulong)40, fixture.Source.Requests[1].KnownRevision!.Position.Value);
    }

    [Fact]
    public async Task ExactRevisionReplayWithDifferentProjectionFails()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(fixture, 40, 0.5m));
        Assert.Equal(1, await fixture.Runtime.RunCycleAsync());

        fixture.Source.Enqueue(Batch(fixture, 40, 0.6m));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Runtime.RunCycleAsync());
    }

    [Fact]
    public async Task ReplayOmittingPreviouslyCommittedEvaluationFails()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(
            fixture,
            40,
            (BuiltInOperationalMetricDefinitions.AvailabilityId, 0.5m),
            (BuiltInOperationalMetricDefinitions.QualityId, 0.9m)));
        Assert.Equal(2, await fixture.Runtime.RunCycleAsync());

        fixture.Source.Enqueue(Batch(
            fixture,
            40,
            (BuiltInOperationalMetricDefinitions.AvailabilityId, 0.5m)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Runtime.RunCycleAsync());
    }

    [Fact]
    public async Task EmptyReplayOfPreviouslyNonEmptyRevisionFails()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(fixture, 40, 0.5m));
        Assert.Equal(1, await fixture.Runtime.RunCycleAsync());

        fixture.Source.Enqueue(new OperationalMetricEvaluationBatch(
            Revision(fixture, 40),
            []));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Runtime.RunCycleAsync());
    }

    [Fact]
    public async Task ReplayAddingEvaluationFails()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(fixture, 40, 0.5m));
        Assert.Equal(1, await fixture.Runtime.RunCycleAsync());

        fixture.Source.Enqueue(Batch(
            fixture,
            40,
            (BuiltInOperationalMetricDefinitions.AvailabilityId, 0.5m),
            (BuiltInOperationalMetricDefinitions.QualityId, 0.9m)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Runtime.RunCycleAsync());
    }

    [Fact]
    public async Task ReorderedReplaySucceedsBecauseBatchOrderIsNonSemantic()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(
            fixture,
            40,
            (BuiltInOperationalMetricDefinitions.AvailabilityId, 0.5m),
            (BuiltInOperationalMetricDefinitions.QualityId, 0.9m)));
        Assert.Equal(2, await fixture.Runtime.RunCycleAsync());

        fixture.Source.Enqueue(Batch(
            fixture,
            40,
            (BuiltInOperationalMetricDefinitions.QualityId, 0.9m),
            (BuiltInOperationalMetricDefinitions.AvailabilityId, 0.5m)));

        Assert.Equal(0, await fixture.Runtime.RunCycleAsync());
    }

    [Fact]
    public async Task RestartRestoresCheckpointAndResumesFromNextRevision()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(fixture, 40, 0.5m));
        Assert.Equal(1, await fixture.Runtime.RunCycleAsync());

        var restartedSource = new QueueEvaluationBatchSource();
        restartedSource.Enqueue(Batch(fixture, 41, 0.625m));
        var restarted = CreateRuntime(fixture, restartedSource);

        Assert.Equal(1, await restarted.RunCycleAsync());
        Assert.Equal((ulong)40, restartedSource.Requests[0].KnownRevision!.Position.Value);

        var checkpoint = await fixture.Store.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.SourceStreamId,
            CancellationToken.None);
        Assert.Equal((ulong)41, checkpoint!.SourceRevision.Position.Value);
    }

    [Fact]
    public async Task EmptyNewerBatchAdvancesCheckpointWithoutProjectionWrites()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(new OperationalMetricEvaluationBatch(
            Revision(fixture, 40),
            []));

        Assert.Equal(0, await fixture.Runtime.RunCycleAsync());

        var checkpoint = await fixture.Store.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.SourceStreamId,
            CancellationToken.None);
        Assert.Equal((ulong)40, checkpoint!.SourceRevision.Position.Value);
        Assert.Empty(checkpoint.BatchManifest.ProjectionKeys);
    }

    [Fact]
    public async Task OlderBatchAfterCheckpointIsRejected()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(fixture, 40, 0.5m));
        Assert.Equal(1, await fixture.Runtime.RunCycleAsync());
        fixture.Source.Enqueue(Batch(fixture, 39, 0.5m));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Runtime.RunCycleAsync());
    }

    [Fact]
    public async Task PreCancelledCycleDoesNotAdvanceProjectionCheckpoint()
    {
        var fixture = CreateFixture();
        fixture.Source.Enqueue(Batch(fixture, 40, 0.5m));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Runtime.RunCycleAsync(cancellation.Token));

        Assert.Null(await fixture.Store.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.SourceStreamId,
            CancellationToken.None));
    }

    private static RuntimeFixture CreateFixture()
    {
        var machineId = MachineId.New();
        var sourceStreamId = MetricInputStreamId.ForMachine(machineId);
        var sourceProcessorId = new MetricAggregationProcessorId("aggregate-m01");
        var projectionProcessorId = new OperationalMetricProjectionProcessorId("metric-projection-m01");
        var source = new QueueEvaluationBatchSource();
        var store = new InMemoryOperationalMetricProjectionStore();
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var factory = new OperationalMetricProjectionFactory(catalog, projectionProcessorId);
        var fixture = new RuntimeFixture(
            machineId,
            sourceStreamId,
            sourceProcessorId,
            projectionProcessorId,
            source,
            store,
            factory,
            null!);
        return fixture with { Runtime = CreateRuntime(fixture, source) };
    }

    private static OperationalMetricProjectionProcessingRuntime CreateRuntime(
        RuntimeFixture fixture,
        IOperationalMetricEvaluationBatchSource source) => new(
            fixture.ProjectionProcessorId,
            fixture.SourceProcessorId,
            fixture.SourceStreamId,
            source,
            fixture.Factory,
            fixture.Store);

    private static OperationalMetricEvaluationBatch Batch(
        RuntimeFixture fixture,
        ulong position,
        decimal value) => Batch(
            fixture,
            position,
            (BuiltInOperationalMetricDefinitions.AvailabilityId, value));

    private static OperationalMetricEvaluationBatch Batch(
        RuntimeFixture fixture,
        ulong position,
        params (OperationalMetricDefinitionId DefinitionId, decimal Value)[] values)
    {
        var revision = Revision(fixture, position);
        return new OperationalMetricEvaluationBatch(
            revision,
            values.Select(value => new OperationalMetricEvaluation(
                EvaluationKey(fixture, value.DefinitionId),
                OperationalMetricEvaluationStatus.Calculated,
                value.Value,
                OperationalMetricUnits.Ratio,
                null,
                null,
                revision,
                [])).ToArray());
    }

    private static OperationalMetricEvaluationBatch BatchWithEvidence(
        RuntimeFixture fixture,
        ulong position,
        decimal value)
    {
        var revision = Revision(fixture, position);
        var key = EvaluationKey(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId);
        var firstTimestamp = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var lastTimestamp = firstTimestamp.AddMinutes(10);
        return new OperationalMetricEvaluationBatch(
            revision,
            [new OperationalMetricEvaluation(
                key,
                OperationalMetricEvaluationStatus.Calculated,
                value,
                OperationalMetricUnits.Ratio,
                null,
                null,
                revision,
                [
                    new MetricOperandEvidence(
                        "ActualProductionTime",
                        new OperationalMetricAggregateSourceIdentity(
                            fixture.SourceProcessorId,
                            fixture.MachineId,
                            key.PeriodId,
                            MetricInputKeys.ActualProductionTime),
                        revision,
                        MetricDimension.Duration,
                        30m,
                        MetricInputFactUnits.Seconds,
                        2,
                        firstTimestamp,
                        lastTimestamp),
                    new MetricOperandEvidence(
                        "PlannedOperatingTime",
                        new OperationalMetricAggregateSourceIdentity(
                            fixture.SourceProcessorId,
                            fixture.MachineId,
                            key.PeriodId,
                            MetricInputKeys.PlannedOperatingTime),
                        revision,
                        MetricDimension.Duration,
                        60m,
                        MetricInputFactUnits.Seconds,
                        3,
                        firstTimestamp,
                        lastTimestamp),
                ])]);
    }

    private static MetricAggregationCheckpoint Revision(
        RuntimeFixture fixture,
        ulong position) => new(
            fixture.SourceProcessorId,
            fixture.SourceStreamId,
            new MetricInputPosition(position));

    private static OperationalMetricEvaluationKey EvaluationKey(
        RuntimeFixture fixture,
        OperationalMetricDefinitionId definitionId) => new(
            fixture.MachineId,
            new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29))),
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

    private sealed class QueueEvaluationBatchSource : IOperationalMetricEvaluationBatchSource
    {
        private readonly Queue<OperationalMetricEvaluationBatch?> _batches = new();

        public List<OperationalMetricEvaluationBatchRequest> Requests { get; } = [];

        public void Enqueue(OperationalMetricEvaluationBatch? batch) => _batches.Enqueue(batch);

        public ValueTask<OperationalMetricEvaluationBatch?> ReadAsync(
            OperationalMetricEvaluationBatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(
                _batches.Count == 0 ? null : _batches.Dequeue());
        }
    }

    private sealed record RuntimeFixture(
        MachineId MachineId,
        MetricInputStreamId SourceStreamId,
        MetricAggregationProcessorId SourceProcessorId,
        OperationalMetricProjectionProcessorId ProjectionProcessorId,
        QueueEvaluationBatchSource Source,
        InMemoryOperationalMetricProjectionStore Store,
        OperationalMetricProjectionFactory Factory,
        OperationalMetricProjectionProcessingRuntime Runtime);
}
