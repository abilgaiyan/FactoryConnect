using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Core.Tests;

public sealed class InMemoryOperationalMetricProjectionStoreTests
{
    [Fact]
    public async Task FirstCommitPublishesProjectionAndCheckpointTogether()
    {
        var fixture = CreateFixture();
        var revision = Revision(fixture, 10);
        var projection = Projection(fixture, revision, 0.75m);
        var checkpoint = Checkpoint(fixture, revision);

        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                checkpoint,
                [projection]),
            CancellationToken.None);

        Assert.Equal(
            checkpoint,
            await fixture.Store.ReadCheckpointAsync(
                fixture.ProjectionProcessorId,
                fixture.SourceStreamId,
                CancellationToken.None));
        Assert.Equal(
            projection,
            await fixture.Store.ReadProjectionAsync(
                fixture.ProjectionProcessorId,
                projection.Key,
                CancellationToken.None));
    }

    [Fact]
    public async Task StaleExpectedCheckpointRejectsEntireCommit()
    {
        var fixture = CreateFixture();
        var revision10 = Revision(fixture, 10);
        var checkpoint10 = Checkpoint(fixture, revision10);
        var projection10 = Projection(fixture, revision10, 0.5m);
        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                checkpoint10,
                [projection10]),
            CancellationToken.None);

        var revision11 = Revision(fixture, 11);
        var projection11 = Projection(fixture, revision11, 0.6m);
        var stale = Checkpoint(fixture, Revision(fixture, 9));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.CommitAsync(
                new OperationalMetricProjectionCommit(
                    fixture.ProjectionProcessorId,
                    stale,
                    Checkpoint(fixture, revision11),
                    [projection11]),
                CancellationToken.None));

        Assert.Equal(
            checkpoint10,
            await fixture.Store.ReadCheckpointAsync(
                fixture.ProjectionProcessorId,
                fixture.SourceStreamId,
                CancellationToken.None));
        Assert.Equal(
            projection10,
            await fixture.Store.ReadProjectionAsync(
                fixture.ProjectionProcessorId,
                projection10.Key,
                CancellationToken.None));
    }

    [Fact]
    public async Task CheckpointOnlyCommitAdvancesSourceRevision()
    {
        var fixture = CreateFixture();
        var checkpoint10 = Checkpoint(fixture, Revision(fixture, 10));
        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                checkpoint10,
                []),
            CancellationToken.None);

        var checkpoint11 = Checkpoint(fixture, Revision(fixture, 11));
        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                checkpoint10,
                checkpoint11,
                []),
            CancellationToken.None);

        Assert.Equal(
            checkpoint11,
            await fixture.Store.ReadCheckpointAsync(
                fixture.ProjectionProcessorId,
                fixture.SourceStreamId,
                CancellationToken.None));
    }

    [Fact]
    public async Task NewerRevisionMayReplaceSameProjectionKey()
    {
        var fixture = CreateFixture();
        var revision10 = Revision(fixture, 10);
        var checkpoint10 = Checkpoint(fixture, revision10);
        var projection10 = Projection(fixture, revision10, 0.5m);
        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                checkpoint10,
                [projection10]),
            CancellationToken.None);

        var revision11 = Revision(fixture, 11);
        var projection11 = Projection(fixture, revision11, 0.625m);
        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                checkpoint10,
                Checkpoint(fixture, revision11),
                [projection11]),
            CancellationToken.None);

        var stored = await fixture.Store.ReadProjectionAsync(
            fixture.ProjectionProcessorId,
            projection11.Key,
            CancellationToken.None);
        Assert.Equal(projection11, stored);
    }

    [Fact]
    public async Task ProjectionProcessorsAreIsolated()
    {
        var fixture = CreateFixture();
        var otherProcessor = new OperationalMetricProjectionProcessorId("metric-projection-m02");
        var revision = Revision(fixture, 10);
        var firstProjection = Projection(fixture, revision, 0.5m);
        var otherProjection = new OperationalMetricProjection(
            otherProcessor,
            firstProjection.Key,
            firstProjection.Status,
            0.8m,
            firstProjection.Unit,
            null,
            null,
            revision);

        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                Checkpoint(fixture, revision),
                [firstProjection]),
            CancellationToken.None);
        await fixture.Store.CommitAsync(
            new OperationalMetricProjectionCommit(
                otherProcessor,
                null,
                new OperationalMetricProjectionCheckpoint(otherProcessor, revision),
                [otherProjection]),
            CancellationToken.None);

        Assert.Equal(
            firstProjection,
            await fixture.Store.ReadProjectionAsync(
                fixture.ProjectionProcessorId,
                firstProjection.Key,
                CancellationToken.None));
        Assert.Equal(
            otherProjection,
            await fixture.Store.ReadProjectionAsync(
                otherProcessor,
                firstProjection.Key,
                CancellationToken.None));
    }

    [Fact]
    public async Task PreCancelledCommitDoesNotMutateStore()
    {
        var fixture = CreateFixture();
        var revision = Revision(fixture, 10);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Store.CommitAsync(
                new OperationalMetricProjectionCommit(
                    fixture.ProjectionProcessorId,
                    null,
                    Checkpoint(fixture, revision),
                    [Projection(fixture, revision, 0.5m)]),
                cancellation.Token));

        Assert.Null(await fixture.Store.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.SourceStreamId,
            CancellationToken.None));
    }

    private static StoreFixture CreateFixture()
    {
        var machineId = MachineId.New();
        var sourceStreamId = MetricInputStreamId.ForMachine(machineId);
        return new StoreFixture(
            machineId,
            sourceStreamId,
            new MetricAggregationProcessorId("aggregate-m01"),
            new OperationalMetricProjectionProcessorId("metric-projection-m01"),
            new InMemoryOperationalMetricProjectionStore());
    }

    private static MetricAggregationCheckpoint Revision(StoreFixture fixture, ulong position) => new(
        fixture.SourceProcessorId,
        fixture.SourceStreamId,
        new MetricInputPosition(position));

    private static OperationalMetricProjectionCheckpoint Checkpoint(
        StoreFixture fixture,
        MetricAggregationCheckpoint revision) => new(
            fixture.ProjectionProcessorId,
            revision);

    private static OperationalMetricProjection Projection(
        StoreFixture fixture,
        MetricAggregationCheckpoint revision,
        decimal value) => new(
            fixture.ProjectionProcessorId,
            new OperationalMetricEvaluationKey(
                fixture.MachineId,
                new OperationalMetricPeriodId.ProductionDay(
                    new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29))),
                BuiltInOperationalMetricDefinitions.AvailabilityId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            value,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision);

    private sealed record StoreFixture(
        MachineId MachineId,
        MetricInputStreamId SourceStreamId,
        MetricAggregationProcessorId SourceProcessorId,
        OperationalMetricProjectionProcessorId ProjectionProcessorId,
        InMemoryOperationalMetricProjectionStore Store);
}
