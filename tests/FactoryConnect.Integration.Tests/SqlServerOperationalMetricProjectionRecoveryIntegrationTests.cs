using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionRecoveryIntegrationTests(SqlServerTestDatabaseFixture fixture)
    : IClassFixture<SqlServerTestDatabaseFixture>
{
    [Fact]
    public async Task AbsentSetAndEquivalentRetryPreserveLiveAuthorityAndNeighborBytes()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        var before = await seed.ReadProtectedAsync();
        var store = new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString);
        var result = await store.RecoverAsync(seed.Request, CancellationToken.None);
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal(5, result.InsertedProjectionCount);
        Assert.Equal(before, await seed.ReadProtectedAsync());
        var recovered = await seed.ReadAllAsync();
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Equivalent,
            (await store.RecoverAsync(seed.Request, CancellationToken.None)).Outcome);
        Assert.Equal(recovered, await seed.ReadAllAsync());
        await seed.AssertCompleteAsync();
        // Exact latest-batch replay still succeeds, without including recovered keys.
        await seed.Store.CommitAsync(seed.LatestCommit, CancellationToken.None);
        Assert.Equal(recovered, await seed.ReadAllAsync());
    }

    [Fact]
    public async Task PartialEquivalentSetInsertsOnlyAbsentMembers()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        await seed.InsertFixtureAsync(seed.Request.Projections.Take(2).ToArray());
        var partial = await seed.ReadTargetExistingAsync();
        var before = await seed.ReadProtectedAsync();
        var result = await new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
            .RecoverAsync(seed.Request, CancellationToken.None);
        Assert.Equal(3, result.InsertedProjectionCount);
        Assert.Equal(before, await seed.ReadProtectedAsync());
        Assert.Equal(partial, await seed.ReadTargetExistingAsync(limitToInitialTwo: true));
        await seed.AssertCompleteAsync();
    }

    [Theory]
    [InlineData("value")]
    [InlineData("revision")]
    [InlineData("evidence")]
    [InlineData("definition")]
    public async Task ConflictingMemberRejectsEntireRepair(string kind)
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        var p = seed.Request.Projections[0];
        var conflict = new OperationalMetricProjection(p.ProcessorId,
            kind == "definition" ? new OperationalMetricEvaluationKey(p.Key.MachineId, p.Key.PeriodId,
                new OperationalMetricDefinitionId("availability", "2.0"), p.Key.ContextKey) : p.Key,
            p.Status, kind == "value" ? 0.123m : p.Value, p.Unit, p.ReasonCode, p.ReasonOperandName,
            kind == "revision" ? seed.LiveRevision : p.SourceRevision,
            kind == "revision" ? [] : kind == "evidence" ? [] : p.OperandEvidence);
        await seed.InsertFixtureAsync([conflict]);
        var before = await seed.ReadAllAsync();
        var result = await new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
            .RecoverAsync(seed.Request, CancellationToken.None);
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Conflict, result.Outcome);
        Assert.Equal(before, await seed.ReadAllAsync());
    }

    [Fact]
    public async Task LatestManifestTargetRejectsMutation()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString, latestIsTarget: true);
        var before = await seed.ReadAllAsync();
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Conflict,
            (await new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
                .RecoverAsync(seed.Request, CancellationToken.None)).Outcome);
        Assert.Equal(before, await seed.ReadAllAsync());
    }

    [Fact]
    public async Task RevisionBeyondCheckpointRejectsEvenWhenRevisionExists()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        // Seed an older live checkpoint in this disposable fixture only.
        await seed.ExecuteAsync("UPDATE dbo.OperationalMetricProjectionCheckpoint SET Position=0 WHERE OperationalMetricProjectionProcessorRowId=@Processor; DELETE FROM dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionProcessorRowId=@Processor; DELETE e FROM dbo.OperationalMetricProjectionEvidence e JOIN dbo.OperationalMetricProjection p ON p.OperationalMetricProjectionRowId=e.OperationalMetricProjectionRowId WHERE p.OperationalMetricProjectionProcessorRowId=@Processor; DELETE FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=@Processor;");
        var before = await seed.ReadAllAsync();
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Conflict,
            (await new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
                .RecoverAsync(seed.Request, CancellationToken.None)).Outcome);
        Assert.Equal(before, await seed.ReadAllAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidInsertionFailureOrCancellationRollsBack(bool cancel)
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        var before = await seed.ReadAllAsync();
        using var cancellation = new CancellationTokenSource();
        var store = new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
        {
            AfterInsert = (count, token) =>
            {
                if (count == 2)
                {
                    if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                    throw new InvalidOperationException("Injected publication failure.");
                }
                return Task.CompletedTask;
            },
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.RecoverAsync(seed.Request, cancellation.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RecoverAsync(seed.Request, cancellation.Token));
        Assert.Equal(before, await seed.ReadAllAsync());
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Recovered,
            (await new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
                .RecoverAsync(seed.Request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task IncompleteOrNeighborRequestIsRejectedBeforePublication()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        Assert.Throws<ArgumentException>(() => new OperationalMetricProjectionRecoveryRequest(
            seed.Request.ProcessorId, seed.Request.ProductionDayId, seed.Request.SourceRevision,
            seed.Request.Projections.Take(4).ToArray()));
        Assert.Throws<ArgumentException>(() => new OperationalMetricProjectionRecoveryRequest(
            seed.Request.ProcessorId, new ProductionDayId(seed.Request.ProductionDayId.SiteId, new DateOnly(2026, 10, 5)),
            seed.Request.SourceRevision, seed.Request.Projections));
    }
}

internal sealed record RecoveryTestData(string ConnectionString, OperationalMetricProjectionRecoveryRequest Request,
    MetricAggregationCheckpoint LiveRevision, OperationalMetricProjectionCommit LatestCommit,
    SqlServerOperationalMetricProjectionStore Store, long ProcessorRowId)
{
    internal static async Task<RecoveryTestData> CreateAsync(string connectionString, bool latestIsTarget = false)
    {
        var machine = MachineId.New();
        var processor = new MetricAggregationProcessorId($"recovery-aggregation-{Guid.NewGuid():N}");
        var pp = new OperationalMetricProjectionProcessorId($"recovery-{Guid.NewGuid():N}");
        var site = new SiteId("CAMPUS-1");
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 4));
        var start = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var shift = new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId("schedule"), new ShiftId("SHIFT-1"), start, start.AddHours(8));
        var inputs = new SqlServerMetricInputStore(connectionString);
        var aggregation = new SqlServerMetricAggregationStore(connectionString);
        var first = await inputs.AppendAsync(new DurableMetricInputAppend(MetricInputStreamId.ForMachine(machine),
            new DurableMetricInputFact { Id = new MetricInputFactId(Guid.NewGuid().ToString("N")), Key = "duration.running",
                Value=100m, Unit="s", StartsAtUtc=start, EndsAtUtc=start.AddMinutes(10), MachineId=machine,
                CompanyId=new CompanyId("company"), SiteId=site, ShiftId=shift.ShiftId,
                ShiftScheduleAssignmentId=shift.ShiftScheduleAssignmentId }, shift, day), CancellationToken.None);
        var r1 = new MetricAggregationCheckpoint(processor, first.StreamId, first.Position);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, null, r1, [first]), CancellationToken.None);
        var neighbor = new ProductionDayId(site, day.BusinessDate.AddDays(1));
        var second = await inputs.AppendAsync(new DurableMetricInputAppend(first.StreamId,
            first.Fact with { Id=new MetricInputFactId(Guid.NewGuid().ToString("N")) }, shift, neighbor), CancellationToken.None);
        var r2 = new MetricAggregationCheckpoint(processor, first.StreamId, second.Position);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, r1, r2, [second]), CancellationToken.None);
        var target = CreateFive(pp, r1, new OperationalMetricPeriodId.ProductionDay(day));
        var latest = CreateFive(pp, r2, new OperationalMetricPeriodId.ProductionDay(latestIsTarget ? day : neighbor));
        var commit = new OperationalMetricProjectionCommit(pp, null, new OperationalMetricProjectionCheckpoint(pp, r2,
            new OperationalMetricProjectionBatchManifest(latest.Select(p => p.Key))), latest);
        var store = new SqlServerOperationalMetricProjectionStore(connectionString);
        await store.CommitAsync(commit, CancellationToken.None);
        var header = await new SqlServerOperationalMetricProjectionCommitTransaction(connectionString)
            .ReadCheckpointHeaderAsync(pp, CancellationToken.None);
        return new(connectionString, new(pp, day, r1, target), r2, commit, store, header!.ProjectionProcessorRowId);
    }

    private static OperationalMetricProjection[] CreateFive(OperationalMetricProjectionProcessorId processor,
        MetricAggregationCheckpoint revision, OperationalMetricPeriodId period)
    {
        var start = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        OperationalMetricEvaluationKey Key(string metric) => new(revision.StreamId.MachineId, period,
            new OperationalMetricDefinitionId(metric, "1.0"), OperationalMetricEvaluationContextKey.Unpartitioned);
        var operand = new OperationalMetricComponentProjectionEvidence("ActualProductionTime",
            new OperationalMetricAggregateSourceIdentity(revision.ProcessorId, revision.StreamId.MachineId, period, "duration.running"),
            revision, MetricDimension.Duration, 100m, "s", 1, start, start.AddMinutes(10));
        var availability = new OperationalMetricProjection(processor, Key("availability"), OperationalMetricEvaluationStatus.Calculated,
            0.5m, "ratio", null, null, revision, [operand]);
        var performance = new OperationalMetricProjection(processor, Key("performance"), OperationalMetricEvaluationStatus.InsufficientEvidence,
            null, "ratio", OperationalMetricEvaluationReasonCode.MissingReferenceTime, "IdealProductionDuration", revision, [operand]);
        var quality = new OperationalMetricProjection(processor, Key("quality"), OperationalMetricEvaluationStatus.InsufficientEvidence,
            null, "ratio", OperationalMetricEvaluationReasonCode.MissingOperand, "GoodQuantity", revision);
        var oee = new OperationalMetricProjection(processor, Key("oee"), OperationalMetricEvaluationStatus.InsufficientEvidence,
            null, "ratio", OperationalMetricEvaluationReasonCode.DependencyInsufficientEvidence, "Performance", revision,
            dependencyEvidence: [new("Availability", availability.Key.DefinitionId, availability),
                new("Performance", performance.Key.DefinitionId, performance), new("Quality", quality.Key.DefinitionId, quality)]);
        var utilization = new OperationalMetricProjection(processor, Key("utilization.elr"), OperationalMetricEvaluationStatus.Calculated,
            0.25m, "ratio", null, null, revision, [operand]);
        return [availability, performance, quality, oee, utilization];
    }

    internal async Task InsertFixtureAsync(IReadOnlyList<OperationalMetricProjection> projections)
    {
        await using var c = new SqlConnection(ConnectionString); await c.OpenAsync();
        await using var transaction = (SqlTransaction)await c.BeginTransactionAsync();
        var context = new SqlServerOperationalMetricProjectionCommitContext(c, transaction, ProcessorRowId,
            SqlServerOperationalMetricProjectionCommitMode.Advance, LiveRevision.Position);
        foreach (var p in projections)
        {
            var model = SqlServerOperationalMetricProjectionRows.Compile(p);
            var id = await SqlServerOperationalMetricProjectionPublication.InsertProjectionAsync(context, model, CancellationToken.None);
            await SqlServerOperationalMetricProjectionPublication.InsertCompleteEvidenceAsync(context,
                [new(id, new(null, model))], CancellationToken.None);
        }
        await transaction.CommitAsync();
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using var c = new SqlConnection(ConnectionString); await c.OpenAsync();
        await using var command = c.CreateCommand(); command.CommandText=sql;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value=ProcessorRowId;
        await command.ExecuteNonQueryAsync();
    }
    internal Task<string> ReadAllAsync() => ReadBytesAsync("1=1", true);
    internal Task<string> ReadProtectedAsync() => ReadBytesAsync("p.ProductionBusinessDate <> '2026-10-04'", true);
    internal Task<string> ReadTargetExistingAsync(bool limitToInitialTwo = false) => ReadBytesAsync(
        "p.ProductionBusinessDate = '2026-10-04'" + (limitToInitialTwo ? " AND p.MetricKey IN ('availability','performance')" : ""), false);
    private async Task<string> ReadBytesAsync(string filter, bool authority)
    {
        await using var c = new SqlConnection(ConnectionString); await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText=$"""
            SELECT
            (SELECT p.* FROM dbo.OperationalMetricProjection p WHERE p.OperationalMetricProjectionProcessorRowId=@Processor AND {filter} ORDER BY p.OperationalMetricProjectionRowId FOR JSON PATH) AS Projections,
            (SELECT e.* FROM dbo.OperationalMetricProjectionEvidence e JOIN dbo.OperationalMetricProjection p ON p.OperationalMetricProjectionRowId=e.OperationalMetricProjectionRowId WHERE p.OperationalMetricProjectionProcessorRowId=@Processor AND {filter} ORDER BY e.OperationalMetricProjectionEvidenceRowId FOR JSON PATH) AS Evidence,
            (SELECT c.* FROM dbo.OperationalMetricProjectionCheckpoint c WHERE c.OperationalMetricProjectionProcessorRowId=@Processor AND @Authority=1 FOR JSON PATH) AS Checkpoint,
            (SELECT m.* FROM dbo.OperationalMetricProjectionManifest m WHERE m.OperationalMetricProjectionProcessorRowId=@Processor AND @Authority=1 ORDER BY m.OperationalMetricProjectionRowId FOR JSON PATH) AS Manifest
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value=ProcessorRowId;
        command.Parameters.Add("@Authority", SqlDbType.Bit).Value=authority;
        return (string)(await command.ExecuteScalarAsync())!;
    }
    internal async Task AssertCompleteAsync()
    {
        var reader = new SqlServerOperationalMetricProjectionQueryReader(ConnectionString);
        var summaries = await reader.ReadPeriodSummariesAsync(Request.ProcessorId, Request.SourceRevision.StreamId.MachineId,
            new OperationalMetricPeriodId.ProductionDay(Request.ProductionDayId), OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);
        Assert.Equal(5, summaries.Count);
        foreach (var p in Request.Projections)
        {
            var actual = await reader.ReadDetailAsync(Request.ProcessorId, p.Key, CancellationToken.None);
            Assert.True(OperationalMetricProjectionEquivalence.AreEquivalent(p, actual!));
        }
    }
}
