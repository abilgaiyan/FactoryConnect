using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionStoreIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EmptyManifestCheckpointRoundTripsWithCanonicalSourceIdentity()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var store = new SqlServerOperationalMetricProjectionStore(_fixture.ConnectionString);
        var commit = CreateCommit(processorId, null, source.Checkpoint, []);

        await store.CommitAsync(commit, CancellationToken.None);

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            source.Checkpoint.StreamId,
            CancellationToken.None);

        Assert.NotNull(checkpoint);
        Assert.Equal(processorId, checkpoint.ProcessorId);
        Assert.Equal(source.Checkpoint, checkpoint.SourceRevision);
        Assert.Empty(checkpoint.BatchManifest.ProjectionKeys);
    }

    [Fact]
    public async Task NonEmptyCommitRoundTripsCheckpointManifestAndProjection()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var key = CreateKey(source.MachineId);
        var projection = CreateProjection(processorId, key, source.Checkpoint, 0.75m);
        var store = new SqlServerOperationalMetricProjectionStore(_fixture.ConnectionString);

        await store.CommitAsync(
            CreateCommit(processorId, null, source.Checkpoint, [projection]),
            CancellationToken.None);

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            source.Checkpoint.StreamId,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(source.Checkpoint, checkpoint.SourceRevision);
        Assert.Equal([key], checkpoint.BatchManifest.ProjectionKeys);

        var actual = await store.ReadProjectionAsync(processorId, key, CancellationToken.None);
        Assert.NotNull(actual);
        Assert.Equal(projection.ProcessorId, actual.ProcessorId);
        Assert.Equal(projection.Key, actual.Key);
        Assert.Equal(projection.Status, actual.Status);
        Assert.Equal(projection.Value, actual.Value);
        Assert.Equal(projection.SourceRevision, actual.SourceRevision);
    }

    [Fact]
    public async Task ReadCheckpointRejectsDifferentSourceStream()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var store = new SqlServerOperationalMetricProjectionStore(_fixture.ConnectionString);
        await store.CommitAsync(
            CreateCommit(processorId, null, source.Checkpoint, []),
            CancellationToken.None);

        var otherStream = MetricInputStreamId.ForMachine(new MachineId(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadCheckpointAsync(processorId, otherStream, CancellationToken.None));
    }

    [Fact]
    public async Task AdvanceAndReplayPreserveExactCurrentCheckpoint()
    {
        var source = await CreateSourceAsync(includeSecond: true);
        var processorId = NewProcessorId();
        var store = new SqlServerOperationalMetricProjectionStore(_fixture.ConnectionString);
        var first = CreateCommit(processorId, null, source.Checkpoint, []);
        await store.CommitAsync(first, CancellationToken.None);

        var expected = first.ProposedCheckpoint;
        var second = CreateCommit(processorId, expected, source.SecondCheckpoint!, []);
        await store.CommitAsync(second, CancellationToken.None);
        await store.CommitAsync(second, CancellationToken.None);

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            source.Checkpoint.StreamId,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(source.SecondCheckpoint, checkpoint.SourceRevision);
        Assert.Empty(checkpoint.BatchManifest.ProjectionKeys);
    }

    private async Task<SourceFixture> CreateSourceAsync(bool includeSecond = false)
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var firstFact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"projection-store-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"projection-store-aggregation-{Guid.NewGuid():N}");
        var first = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            firstFact.StreamId,
            firstFact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, null, first, []),
            CancellationToken.None);

        if (!includeSecond)
        {
            return new SourceFixture(machineId, first, null);
        }

        var secondFact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"projection-store-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var second = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            secondFact.StreamId,
            secondFact.Position);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, first, second, []),
            CancellationToken.None);
        return new SourceFixture(machineId, first, second);
    }

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var start = new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = 60m,
            Unit = "seconds",
            StartsAtUtc = start,
            EndsAtUtc = start.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            ShiftId = shiftId,
            ShiftScheduleAssignmentId = scheduleId,
        };
        return new DurableMetricInputAppend(
            MetricInputStreamId.ForMachine(machineId),
            fact,
            new ShiftOccurrenceId(siteId, scheduleId, shiftId, start, start.AddHours(8)),
            new ProductionDayId(siteId, DateOnly.FromDateTime(start.UtcDateTime)));
    }

    private static OperationalMetricEvaluationKey CreateKey(MachineId machineId)
    {
        var start = new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
        return new OperationalMetricEvaluationKey(
            machineId,
            new OperationalMetricPeriodId.Shift(
                new ShiftOccurrenceId(
                    new SiteId("SITE-1"),
                    new ShiftScheduleAssignmentId("SCHEDULE-A"),
                    new ShiftId("SHIFT-A"),
                    start,
                    start.AddHours(8))),
            new OperationalMetricDefinitionId("availability", "1"),
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private static OperationalMetricProjection CreateProjection(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision,
        decimal value) =>
        new(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            value,
            "ratio",
            null,
            null,
            revision);

    private static OperationalMetricProjectionCommit CreateCommit(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricProjectionCheckpoint? expected,
        MetricAggregationCheckpoint proposed,
        IReadOnlyList<OperationalMetricProjection> projections) =>
        new(
            processorId,
            expected,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                proposed,
                new OperationalMetricProjectionBatchManifest(
                    projections.Select(static projection => projection.Key))),
            projections);

    private static OperationalMetricProjectionProcessorId NewProcessorId() =>
        new($"projection-store-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint,
        MetricAggregationCheckpoint? SecondCheckpoint);
}
