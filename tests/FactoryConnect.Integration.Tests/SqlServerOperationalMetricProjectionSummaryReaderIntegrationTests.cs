using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionSummaryReaderIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionSummaryReaderIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadPeriodSummariesAsyncReconstructsExactCurrentPublicationInMetricOrder()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var alphaKey = CreateKey(source.MachineId, period, "alpha", "2", context);
        var betaKey = CreateKey(source.MachineId, period, "beta", "1", context);
        var zetaKey = CreateKey(source.MachineId, period, "zeta", "1", context);

        var alpha = CreateCalculated(processorId, alphaKey, source.Checkpoint, 0.125m);
        var beta = new OperationalMetricProjection(
            processorId,
            betaKey,
            OperationalMetricEvaluationStatus.Unavailable,
            null,
            "ratio",
            OperationalMetricEvaluationReasonCode.MissingOperand,
            "runtime",
            source.Checkpoint);
        var zeta = CreateCalculated(processorId, zetaKey, source.Checkpoint, 0.875m);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [zeta, beta, alpha]));

        var reader = new SqlServerOperationalMetricProjectionSummaryReader(_fixture.ConnectionString);
        var summaries = await reader.ReadPeriodSummariesAsync(
            processorId,
            source.MachineId,
            period,
            context,
            CancellationToken.None);

        Assert.Equal(3, summaries.Count);
        Assert.Equal(["alpha", "beta", "zeta"], summaries.Select(static summary => summary.Key.DefinitionId.MetricKey));

        var alphaSummary = summaries[0];
        Assert.Equal(processorId, alphaSummary.ProcessorId);
        Assert.Equal(alphaKey, alphaSummary.Key);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, alphaSummary.Status);
        Assert.Equal(0.125m, alphaSummary.Value);
        Assert.Equal("ratio", alphaSummary.Unit);
        Assert.Null(alphaSummary.ReasonCode);
        Assert.Null(alphaSummary.ReasonOperandName);
        Assert.Equal(source.Checkpoint, alphaSummary.SourceRevision);

        var betaSummary = summaries[1];
        Assert.Equal(betaKey, betaSummary.Key);
        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, betaSummary.Status);
        Assert.Null(betaSummary.Value);
        Assert.Equal("ratio", betaSummary.Unit);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, betaSummary.ReasonCode);
        Assert.Equal("runtime", betaSummary.ReasonOperandName);
        Assert.Equal(source.Checkpoint, betaSummary.SourceRevision);
    }

    [Fact]
    public async Task ReadPeriodSummariesAsyncUsesManifestAsCurrentPublicationAuthority()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var visibleKey = CreateKey(source.MachineId, period, "availability", "1", context);
        var hiddenKey = CreateKey(source.MachineId, period, "performance", "1", context);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [
                CreateCalculated(processorId, visibleKey, source.Checkpoint, 0.5m),
                CreateCalculated(processorId, hiddenKey, source.Checkpoint, 0.8m),
            ]));

        var header = await ReadCheckpointHeaderAsync(processorId);
        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE m
                FROM dbo.OperationalMetricProjectionManifest AS m
                INNER JOIN dbo.OperationalMetricProjection AS p
                    ON p.OperationalMetricProjectionProcessorRowId = m.OperationalMetricProjectionProcessorRowId
                    AND p.OperationalMetricProjectionRowId = m.OperationalMetricProjectionRowId
                WHERE m.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                    AND p.MetricKey = N'performance';
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var reader = new SqlServerOperationalMetricProjectionSummaryReader(_fixture.ConnectionString);
        var summaries = await reader.ReadPeriodSummariesAsync(
            processorId,
            source.MachineId,
            period,
            context,
            CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal("availability", summary.Key.DefinitionId.MetricKey);

        await using var verificationConnection = _fixture.CreateConnection();
        await verificationConnection.OpenAsync();
        await using var verification = verificationConnection.CreateCommand();
        verification.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.OperationalMetricProjection
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                AND MetricKey = N'performance';
            """;
        verification.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
        Assert.Equal(1L, Convert.ToInt64(await verification.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ReadPeriodSummariesAsyncFailsWhenFilteredOutManifestRowHasCorruptOrderKey()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var requestedPeriod = CreateShiftPeriod();
        var otherPeriod = new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 9, 7)));
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var requestedKey = CreateKey(source.MachineId, requestedPeriod, "availability", "1", context);
        var filteredOutKey = CreateKey(source.MachineId, otherPeriod, "performance", "1", context);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [
                CreateCalculated(processorId, requestedKey, source.Checkpoint, 0.5m),
                CreateCalculated(processorId, filteredOutKey, source.Checkpoint, 0.8m),
            ]));

        var header = await ReadCheckpointHeaderAsync(processorId);
        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dbo.OperationalMetricProjection
                SET MetricKeyOrderKey = 0x00
                WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
                    AND MetricKey = N'performance';
                """;
            command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = header.ProjectionProcessorRowId;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var reader = new SqlServerOperationalMetricProjectionSummaryReader(_fixture.ConnectionString);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadPeriodSummariesAsync(
                processorId,
                source.MachineId,
                requestedPeriod,
                context,
                CancellationToken.None));

        Assert.Contains("corrupt", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task PublishAsync(OperationalMetricProjectionCommit commit)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    cancellationToken),
            CancellationToken.None);
    }

    private async Task<SqlServerOperationalMetricProjectionCheckpointHeader> ReadCheckpointHeaderAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var header = await transaction.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        return Assert.IsType<SqlServerOperationalMetricProjectionCheckpointHeader>(header);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"summary-reader-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"summary-reader-aggregation-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, null, checkpoint, []),
            CancellationToken.None);
        return new SourceFixture(machineId, checkpoint);
    }

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var start = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = 1m,
            Unit = "seconds",
            StartsAtUtc = start,
            EndsAtUtc = start.AddSeconds(1),
            CompanyId = new CompanyId("COMPANY-1"),
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

    private static OperationalMetricPeriodId CreateShiftPeriod() =>
        new OperationalMetricPeriodId.Shift(
            new ShiftOccurrenceId(
                new SiteId("SITE-1"),
                new ShiftScheduleAssignmentId("SCHEDULE-A"),
                new ShiftId("SHIFT-A"),
                new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero)));

    private static OperationalMetricEvaluationKey CreateKey(
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        string metricKey,
        string version,
        OperationalMetricEvaluationContextKey contextKey) =>
        new(
            machineId,
            periodId,
            new OperationalMetricDefinitionId(metricKey, version),
            contextKey);

    private static OperationalMetricProjection CreateCalculated(
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

    private static OperationalMetricProjectionCommit CreateInitialCommit(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint revision,
        IReadOnlyList<OperationalMetricProjection> projections) =>
        new(
            processorId,
            null,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                revision,
                new OperationalMetricProjectionBatchManifest(
                    projections.Select(static projection => projection.Key))),
            projections);

    private static OperationalMetricProjectionProcessorId NewProcessorId() =>
        new($"summary-reader-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
