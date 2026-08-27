using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerProductionContextHandoffIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerProductionContextHandoffIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ActivityOutputsPositionedFactAndCheckpointCommitTogether()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var streamId = new ObservationStreamId(machineId, "activity-source");
        var processorId = new ObservationProcessorId($"activity-{Guid.NewGuid():N}");
        var append = CreateAppend(machineId, "activity-fact", 10m, minute: 0);
        var context = CreateContext(machineId, "context-1", processorId, streamId, 1UL, minute: 0);
        var eligibility = CreateEligibility(machineId, "eligibility-1", context, minute: 0);
        var store = new SqlServerProductionContextProcessingStore(_fixture.ConnectionString);

        await store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = new ObservationProcessingCheckpoint(
                    processorId,
                    streamId,
                    new ObservationPosition(1)),
                ContextualizedActivity = [context],
                EligibilityIntervals = [eligibility],
                MetricFacts = [append.Fact],
                MetricInputs = [append],
            },
            CancellationToken.None);

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            streamId,
            CancellationToken.None);
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var batch = await inputStore.ReadAsync(
            new MetricInputReadRequest(append.StreamId, null, 10),
            CancellationToken.None);

        Assert.Equal(1UL, checkpoint!.Position.Value);
        Assert.Single(batch.Facts);
        Assert.Equal(append.Fact, batch.Facts[0].Fact);
        Assert.True(await OutputExistsAsync(
            "dbo.ContextualizedActivityOutput",
            context.Id.Value));
        Assert.True(await OutputExistsAsync(
            "dbo.ProductionTimeEligibilityOutput",
            eligibility.Id.Value));
    }

    [Fact]
    public async Task QuantityOutputAndCheckpointCommitTogether()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var streamId = new ObservationStreamId(machineId, "quantity-source");
        var processorId = new ObservationProcessorId($"quantity-{Guid.NewGuid():N}");
        var append = CreateAppend(machineId, "quantity-fact", 1m, minute: 10);
        var store = new SqlServerProductionContextProcessingStore(_fixture.ConnectionString);

        await store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = new ObservationProcessingCheckpoint(
                    processorId,
                    streamId,
                    new ObservationPosition(1)),
                MetricFacts = [append.Fact],
                MetricInputs = [append],
            },
            CancellationToken.None);

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            streamId,
            CancellationToken.None);
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var batch = await inputStore.ReadAsync(
            new MetricInputReadRequest(append.StreamId, null, 10),
            CancellationToken.None);

        Assert.Equal(1UL, checkpoint!.Position.Value);
        Assert.Single(batch.Facts);
        Assert.Equal(1UL, batch.Facts[0].Position.Value);
    }

    [Fact]
    public async Task FailureAfterPositionAllocationRollsBackFactAndCheckpoint()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var seedProcessor = new ObservationProcessorId($"seed-{Guid.NewGuid():N}");
        var seedStream = new ObservationStreamId(machineId, "seed-source");
        var seedContext = CreateContext(machineId, "shared-context", seedProcessor, seedStream, 1UL, minute: 20);
        var store = new SqlServerProductionContextProcessingStore(_fixture.ConnectionString);

        await store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = new ObservationProcessingCheckpoint(
                    seedProcessor,
                    seedStream,
                    new ObservationPosition(1)),
                ContextualizedActivity = [seedContext],
            },
            CancellationToken.None);

        var processorId = new ObservationProcessorId($"failing-{Guid.NewGuid():N}");
        var sourceStream = new ObservationStreamId(machineId, "failing-source");
        var append = CreateAppend(machineId, "rolled-back-fact", 5m, minute: 21);
        var conflictingContext = seedContext with { State = MachineState.Fault };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitAsync(
                new ProductionContextProcessingCommit
                {
                    ExpectedCheckpoint = null,
                    NextCheckpoint = new ObservationProcessingCheckpoint(
                        processorId,
                        sourceStream,
                        new ObservationPosition(1)),
                    ContextualizedActivity = [conflictingContext],
                    MetricFacts = [append.Fact],
                    MetricInputs = [append],
                },
                CancellationToken.None));

        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            sourceStream,
            CancellationToken.None);
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var batch = await inputStore.ReadAsync(
            new MetricInputReadRequest(append.StreamId, null, 10),
            CancellationToken.None);

        Assert.Null(checkpoint);
        Assert.Empty(batch.Facts);
    }

    [Fact]
    public async Task IdenticalReplayPreservesPositionAndConflictingReplayDoesNotAdvance()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var sourceStream = new ObservationStreamId(machineId, "replay-source");
        var processorId = new ObservationProcessorId($"replay-{Guid.NewGuid():N}");
        var append = CreateAppend(machineId, "replay-fact", 7m, minute: 30);
        var store = new SqlServerProductionContextProcessingStore(_fixture.ConnectionString);
        var firstCheckpoint = new ObservationProcessingCheckpoint(
            processorId,
            sourceStream,
            new ObservationPosition(1));

        await store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = firstCheckpoint,
                MetricFacts = [append.Fact],
                MetricInputs = [append],
            },
            CancellationToken.None);

        var secondCheckpoint = new ObservationProcessingCheckpoint(
            processorId,
            sourceStream,
            new ObservationPosition(2));
        await store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = firstCheckpoint,
                NextCheckpoint = secondCheckpoint,
                MetricFacts = [append.Fact],
                MetricInputs = [append],
            },
            CancellationToken.None);

        var conflictingFact = append.Fact with { Value = 999m };
        var conflictingAppend = new DurableMetricInputAppend(
            append.StreamId,
            conflictingFact,
            append.ShiftOccurrenceId,
            append.ProductionDayId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitAsync(
                new ProductionContextProcessingCommit
                {
                    ExpectedCheckpoint = secondCheckpoint,
                    NextCheckpoint = new ObservationProcessingCheckpoint(
                        processorId,
                        sourceStream,
                        new ObservationPosition(3)),
                    MetricFacts = [conflictingFact],
                    MetricInputs = [conflictingAppend],
                },
                CancellationToken.None));

        var restored = await store.ReadCheckpointAsync(
            processorId,
            sourceStream,
            CancellationToken.None);
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var batch = await inputStore.ReadAsync(
            new MetricInputReadRequest(append.StreamId, null, 10),
            CancellationToken.None);

        Assert.Equal(2UL, restored!.Position.Value);
        Assert.Single(batch.Facts);
        Assert.Equal(1UL, batch.Facts[0].Position.Value);
        Assert.Equal(7m, batch.Facts[0].Fact.Value);
    }

    [Fact]
    public async Task ActivityAndQuantityProgressIndependentlyOnOneDownstreamSequence()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var activityProcessor = new ObservationProcessorId($"activity-{Guid.NewGuid():N}");
        var quantityProcessor = new ObservationProcessorId($"quantity-{Guid.NewGuid():N}");
        var activityStream = new ObservationStreamId(machineId, "activity-source-independent");
        var quantityStream = new ObservationStreamId(machineId, "quantity-source-independent");
        var activityAppend = CreateAppend(machineId, "activity-sequence", 4m, minute: 40);
        var quantityAppend = CreateAppend(machineId, "quantity-sequence", 1m, minute: 41);
        var store = new SqlServerProductionContextProcessingStore(_fixture.ConnectionString);

        await store.CommitAsync(
            CreateMetricCommit(activityProcessor, activityStream, 10UL, activityAppend),
            CancellationToken.None);
        await store.CommitAsync(
            CreateMetricCommit(quantityProcessor, quantityStream, 20UL, quantityAppend),
            CancellationToken.None);

        var activityCheckpoint = await store.ReadCheckpointAsync(
            activityProcessor,
            activityStream,
            CancellationToken.None);
        var quantityCheckpoint = await store.ReadCheckpointAsync(
            quantityProcessor,
            quantityStream,
            CancellationToken.None);
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var batch = await inputStore.ReadAsync(
            new MetricInputReadRequest(activityAppend.StreamId, null, 10),
            CancellationToken.None);

        Assert.Equal(10UL, activityCheckpoint!.Position.Value);
        Assert.Equal(20UL, quantityCheckpoint!.Position.Value);
        Assert.Equal(2, batch.Facts.Count);
        Assert.Equal(1UL, batch.Facts[0].Position.Value);
        Assert.Equal(2UL, batch.Facts[1].Position.Value);
    }

    private static ProductionContextProcessingCommit CreateMetricCommit(
        ObservationProcessorId processorId,
        ObservationStreamId sourceStream,
        ulong checkpoint,
        DurableMetricInputAppend append) =>
        new()
        {
            ExpectedCheckpoint = null,
            NextCheckpoint = new ObservationProcessingCheckpoint(
                processorId,
                sourceStream,
                new ObservationPosition(checkpoint)),
            MetricFacts = [append.Fact],
            MetricInputs = [append],
        };

    private static DurableMetricInputAppend CreateAppend(
        MachineId machineId,
        string factId,
        decimal value,
        int minute)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart = new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var factStart = occurrenceStart.AddMinutes(minute);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = value,
            Unit = "seconds",
            StartsAtUtc = factStart,
            EndsAtUtc = factStart.AddMinutes(1),
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
            new ShiftOccurrenceId(
                siteId,
                scheduleId,
                shiftId,
                occurrenceStart,
                occurrenceStart.AddHours(8)),
            new ProductionDayId(siteId, new DateOnly(2026, 8, 27)));
    }

    private static ContextualizedActivityInterval CreateContext(
        MachineId machineId,
        string id,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ulong position,
        int minute)
    {
        var start = new DateTimeOffset(2026, 8, 27, 6, minute, 0, TimeSpan.Zero);
        return new ContextualizedActivityInterval
        {
            Id = new ContextualizedActivityIntervalId(id),
            SourceProcessorId = processorId,
            SourcePosition = new ObservationPosition(position),
            SourceStreamId = streamId,
            SourceInstanceId = 1,
            SourceSequence = position,
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            State = MachineState.Running,
            StartsAtUtc = start,
            EndsAtUtc = start.AddMinutes(1),
            ShiftId = new ShiftId("SHIFT-A"),
            ShiftScheduleAssignmentId = new ShiftScheduleAssignmentId("SCHEDULE-A"),
        };
    }

    private static ProductionTimeEligibilityInterval CreateEligibility(
        MachineId machineId,
        string id,
        ContextualizedActivityInterval context,
        int minute)
    {
        var start = new DateTimeOffset(2026, 8, 27, 6, minute, 0, TimeSpan.Zero);
        return new ProductionTimeEligibilityInterval
        {
            Id = new ProductionTimeEligibilityIntervalId(id),
            SourceContextualizedActivityIntervalId = context.Id,
            CompanyId = context.CompanyId,
            SiteId = context.SiteId,
            ProductionLineId = context.ProductionLineId,
            MachineId = machineId,
            State = context.State,
            ShiftId = context.ShiftId,
            ShiftScheduleAssignmentId = context.ShiftScheduleAssignmentId,
            StartsAtUtc = start,
            EndsAtUtc = start.AddMinutes(1),
            IsPlannedProductionTime = true,
        };
    }

    private async Task<bool> OutputExistsAsync(string tableName, string identity)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(1) FROM {tableName} WHERE IdentityBinary = @Identity;";
        command.Parameters.Add("@Identity", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(identity);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        return count == 1;
    }
}
