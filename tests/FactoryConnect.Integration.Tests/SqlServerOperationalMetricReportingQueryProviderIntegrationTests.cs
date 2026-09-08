using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricReportingQueryProviderIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricReportingQueryProviderIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadWindowAsyncMatchesProviderNeutralQuerySemanticsExactly()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var unpartitioned = OperationalMetricEvaluationContextKey.Unpartitioned;
        var contextual = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("PO-1"),
        };
        var firstPeriod = CreateShiftPeriod(6);
        var secondPeriod = CreateShiftPeriod(14);
        var productionDay = new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 9, 7)));

        var projections = new[]
        {
            CreateCalculated(processorId, CreateKey(source.MachineId, firstPeriod, "availability", "1", unpartitioned), source.Checkpoint, 0.5m),
            CreateCalculated(processorId, CreateKey(source.MachineId, secondPeriod, "availability", "1", contextual), source.Checkpoint, 0.6m),
            CreateCalculated(processorId, CreateKey(source.MachineId, secondPeriod, "performance", "1", unpartitioned), source.Checkpoint, 0.7m),
            CreateUnavailable(processorId, CreateKey(source.MachineId, secondPeriod, "availability", "2", unpartitioned), source.Checkpoint),
            CreateCalculated(processorId, CreateKey(source.MachineId, productionDay, "availability", "1", unpartitioned), source.Checkpoint, 0.8m),
        };
        await PublishAsync(CreateInitialCommit(processorId, source.Checkpoint, projections));

        var query = new ShiftOperationalMetricReportQuery(
            Sources((source.MachineId, processorId)),
            firstPeriod.ShiftOccurrenceId.StartsAtUtc,
            secondPeriod.ShiftOccurrenceId.EndsAtUtc,
            new OperationalMetricDefinitionSelection([
                new OperationalMetricDefinitionId("availability", "1"),
            ]),
            new OperationalMetricContextFilter { UnpartitionedOnly = true },
            new OperationalMetricStatusSelection([OperationalMetricEvaluationStatus.Calculated]),
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(200));

        var comparer = OperationalMetricReportOrdering.GetEvaluationKeyComparer(query.Order);
        var expected = projections
            .Select(static projection => new OperationalMetricProjectionSummary(projection))
            .Where(summary => OperationalMetricReportQuerySemantics.Matches(query, summary))
            .OrderBy(static summary => summary.Key, comparer)
            .ToArray();

        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);
        var actual = await provider.ReadWindowAsync(query, null, 200, CancellationToken.None);

        Assert.Equal(expected.Select(static summary => summary.Key), actual.Select(static summary => summary.Key));
        Assert.All(actual, summary => Assert.True(OperationalMetricReportQuerySemantics.Matches(query, summary)));
    }

    [Theory]
    [InlineData(OperationalMetricReportOrder.PeriodAscending)]
    [InlineData(OperationalMetricReportOrder.PeriodDescending)]
    public async Task ReadWindowAsyncUsesProviderNeutralTotalOrdering(OperationalMetricReportOrder order)
    {
        var first = await CreateSourceAsync();
        var second = await CreateSourceAsync();
        var firstProcessor = NewProcessorId();
        var secondProcessor = NewProcessorId();
        var periods = new[] { CreateShiftPeriod(6), CreateShiftPeriod(14), CreateShiftPeriod(22) };

        var firstProjections = new[]
        {
            CreateCalculated(firstProcessor, CreateKey(first.MachineId, periods[2], "beta", "1", OperationalMetricEvaluationContextKey.Unpartitioned), first.Checkpoint, 0.7m),
            CreateCalculated(firstProcessor, CreateKey(first.MachineId, periods[0], "alpha", "1", OperationalMetricEvaluationContextKey.Unpartitioned), first.Checkpoint, 0.5m),
        };
        var secondProjections = new[]
        {
            CreateCalculated(secondProcessor, CreateKey(second.MachineId, periods[1], "alpha", "2", OperationalMetricEvaluationContextKey.Unpartitioned), second.Checkpoint, 0.6m),
            CreateCalculated(secondProcessor, CreateKey(second.MachineId, periods[0], "gamma", "1", OperationalMetricEvaluationContextKey.Unpartitioned), second.Checkpoint, 0.8m),
        };

        await PublishAsync(CreateInitialCommit(firstProcessor, first.Checkpoint, firstProjections));
        await PublishAsync(CreateInitialCommit(secondProcessor, second.Checkpoint, secondProjections));

        var query = CreateShiftQuery(
            Sources((first.MachineId, firstProcessor), (second.MachineId, secondProcessor)),
            order);
        var comparer = OperationalMetricReportOrdering.GetEvaluationKeyComparer(order);
        var expected = firstProjections
            .Concat(secondProjections)
            .Select(static projection => new OperationalMetricProjectionSummary(projection))
            .Where(summary => OperationalMetricReportQuerySemantics.Matches(query, summary))
            .OrderBy(static summary => summary.Key, comparer)
            .Select(static summary => summary.Key)
            .ToArray();

        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);
        var actual = await provider.ReadWindowAsync(query, null, 200, CancellationToken.None);

        Assert.Equal(expected, actual.Select(static summary => summary.Key));
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("alpha", 2)]
    [InlineData("beta", 1)]
    [InlineData("gamma", 0)]
    [InlineData("delta", 1)]
    public async Task ReadWindowAsyncUsesStrictProviderNeutralSeek(string? cursorMetricKey, int expectedCount)
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod(6);
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var projections = new[]
        {
            CreateCalculated(processorId, CreateKey(source.MachineId, period, "gamma", "1", context), source.Checkpoint, 0.3m),
            CreateCalculated(processorId, CreateKey(source.MachineId, period, "alpha", "1", context), source.Checkpoint, 0.1m),
            CreateCalculated(processorId, CreateKey(source.MachineId, period, "beta", "1", context), source.Checkpoint, 0.2m),
        };
        await PublishAsync(CreateInitialCommit(processorId, source.Checkpoint, projections));

        var query = CreateShiftQuery(Sources((source.MachineId, processorId)), OperationalMetricReportOrder.PeriodAscending);
        var startAfter = cursorMetricKey is null
            ? null
            : CreateKey(source.MachineId, period, cursorMetricKey, "1", context);
        var comparer = OperationalMetricReportOrdering.GetEvaluationKeyComparer(query.Order);
        var expected = projections
            .Select(static projection => new OperationalMetricProjectionSummary(projection))
            .Where(summary => OperationalMetricReportQuerySemantics.Matches(query, summary))
            .Where(summary => startAfter is null || comparer.Compare(summary.Key, startAfter) > 0)
            .OrderBy(static summary => summary.Key, comparer)
            .ToArray();

        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);
        var actual = await provider.ReadWindowAsync(query, startAfter, 200, CancellationToken.None);

        Assert.Equal(expectedCount, actual.Count);
        Assert.Equal(expected.Select(static summary => summary.Key), actual.Select(static summary => summary.Key));
    }

    [Fact]
    public async Task ReadWindowAsyncValidatesMaximumCountAndBoundsResult()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod(6);
        var projections = new[]
        {
            CreateCalculated(processorId, CreateKey(source.MachineId, period, "alpha", "1", OperationalMetricEvaluationContextKey.Unpartitioned), source.Checkpoint, 0.1m),
            CreateCalculated(processorId, CreateKey(source.MachineId, period, "beta", "1", OperationalMetricEvaluationContextKey.Unpartitioned), source.Checkpoint, 0.2m),
            CreateCalculated(processorId, CreateKey(source.MachineId, period, "gamma", "1", OperationalMetricEvaluationContextKey.Unpartitioned), source.Checkpoint, 0.3m),
        };
        await PublishAsync(CreateInitialCommit(processorId, source.Checkpoint, projections));

        var query = CreateShiftQuery(Sources((source.MachineId, processorId)), OperationalMetricReportOrder.PeriodAscending);
        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await provider.ReadWindowAsync(query, null, 0, CancellationToken.None));

        var actual = await provider.ReadWindowAsync(query, null, 2, CancellationToken.None);
        Assert.Equal(2, actual.Count);
    }

    [Fact]
    public async Task ReadWindowAsyncComposesProcessorsAndAcceptsEmptyCurrentPublication()
    {
        var first = await CreateSourceAsync();
        var second = await CreateSourceAsync();
        var firstProcessor = NewProcessorId();
        var secondProcessor = NewProcessorId();
        var period = CreateShiftPeriod(6);

        var firstProjection = CreateCalculated(
            firstProcessor,
            CreateKey(first.MachineId, period, "availability", "1", OperationalMetricEvaluationContextKey.Unpartitioned),
            first.Checkpoint,
            0.5m);
        await PublishAsync(CreateInitialCommit(firstProcessor, first.Checkpoint, [firstProjection]));
        await PublishAsync(CreateInitialCommit(secondProcessor, second.Checkpoint, []));

        var query = CreateShiftQuery(
            Sources((first.MachineId, firstProcessor), (second.MachineId, secondProcessor)),
            OperationalMetricReportOrder.PeriodAscending);
        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);

        var actual = await provider.ReadWindowAsync(query, null, 200, CancellationToken.None);

        var summary = Assert.Single(actual);
        Assert.Equal(firstProjection.Key, summary.Key);
    }

    [Fact]
    public async Task ReadWindowAsyncDoesNotHideCorruptManifestRowBehindReportFilter()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod(6);
        var visible = CreateCalculated(
            processorId,
            CreateKey(source.MachineId, period, "availability", "1", OperationalMetricEvaluationContextKey.Unpartitioned),
            source.Checkpoint,
            0.5m);
        var filteredOut = CreateCalculated(
            processorId,
            CreateKey(source.MachineId, period, "performance", "1", OperationalMetricEvaluationContextKey.Unpartitioned),
            source.Checkpoint,
            0.8m);
        await PublishAsync(CreateInitialCommit(processorId, source.Checkpoint, [visible, filteredOut]));

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

        var query = new ShiftOperationalMetricReportQuery(
            Sources((source.MachineId, processorId)),
            period.ShiftOccurrenceId.StartsAtUtc,
            period.ShiftOccurrenceId.EndsAtUtc,
            new OperationalMetricDefinitionSelection([
                new OperationalMetricDefinitionId("availability", "1"),
            ]),
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(200));
        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.ReadWindowAsync(query, null, 200, CancellationToken.None));

        Assert.Contains("corrupt", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ShiftOperationalMetricReportQuery CreateShiftQuery(
        OperationalMetricReportingSourceSelection sources,
        OperationalMetricReportOrder order) =>
        new(
            sources,
            new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero),
            null,
            null,
            null,
            order,
            new ReportingPageRequest(200));

    private static OperationalMetricReportingSourceSelection Sources(
        params (MachineId MachineId, OperationalMetricProjectionProcessorId ProcessorId)[] sources) =>
        new(sources.Select(static source =>
            new OperationalMetricReportingSource(source.MachineId, source.ProcessorId)));

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
            CreateAppend(machineId, $"reporting-window-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"reporting-window-aggregation-{Guid.NewGuid():N}");
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

    private static OperationalMetricPeriodId.Shift CreateShiftPeriod(int startHour) =>
        new(
            new ShiftOccurrenceId(
                new SiteId("SITE-1"),
                new ShiftScheduleAssignmentId($"SCHEDULE-{startHour}"),
                new ShiftId($"SHIFT-{startHour}"),
                new DateTimeOffset(2026, 9, 7, startHour % 24, 0, 0, TimeSpan.Zero).AddDays(startHour / 24),
                new DateTimeOffset(2026, 9, 7, startHour % 24, 0, 0, TimeSpan.Zero).AddDays(startHour / 24).AddHours(8)));

    private static OperationalMetricEvaluationKey CreateKey(
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        string metricKey,
        string version,
        OperationalMetricEvaluationContextKey contextKey) =>
        new(machineId, periodId, new OperationalMetricDefinitionId(metricKey, version), contextKey);

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

    private static OperationalMetricProjection CreateUnavailable(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision) =>
        new(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Unavailable,
            null,
            "ratio",
            OperationalMetricEvaluationReasonCode.MissingOperand,
            "runtime",
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
        new($"reporting-window-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
