using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricReportingQueryReaderTests
{
    [Fact]
    public async Task InMemoryReaderFiltersMultipleExactMachineProcessorSources()
    {
        var fixture = CreateFixture();
        var day = new DateOnly(2026, 8, 29);
        var first = QueryFixture.Projection(fixture.SourceA, day, "Availability", "1.0", 0.75m);
        var second = QueryFixture.Projection(fixture.SourceB, day, "OEE", "1.0", 0.65m);
        var wrongProcessor = QueryFixture.Projection(
            new OperationalMetricReportingSource(
                fixture.SourceA.MachineId,
                new OperationalMetricProjectionProcessorId("processor-a-other")),
            day,
            "OEE",
            "1.0",
            0.1m);
        await fixture.SeedAsync(first, second, wrongProcessor);

        var page = await fixture.Reader.ReadAsync(
            fixture.Query(day, day.AddDays(1), 20),
            CancellationToken.None);

        Assert.Collection(
            page.Items,
            item => Assert.Equal(first.Key, item.Key),
            item => Assert.Equal(second.Key, item.Key));
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task PageSizeOneTraversesCanonicalTiesWithoutDuplicatesOrOmissions()
    {
        var fixture = CreateFixture();
        var day = new DateOnly(2026, 8, 29);
        var expected = new[]
        {
            QueryFixture.Projection(fixture.SourceA, day, "Availability", "1.0", 0.7m),
            QueryFixture.Projection(fixture.SourceA, day, "Availability", "2.0", 0.71m),
            QueryFixture.Projection(fixture.SourceA, day, "OEE", "1.0", 0.6m),
            QueryFixture.Projection(fixture.SourceB, day, "Availability", "1.0", 0.8m),
            QueryFixture.Projection(fixture.SourceA, day.AddDays(1), "Availability", "1.0", 0.72m),
        };
        await fixture.SeedAsync(expected);
        var actual = new List<OperationalMetricProjectionSummary>();
        ReportingContinuationToken? token = null;

        do
        {
            var page = await fixture.Reader.ReadAsync(
                fixture.Query(day, day.AddDays(2), 1, token),
                CancellationToken.None);
            actual.AddRange(page.Items);
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(expected.Select(static projection => projection.Key), actual.Select(static item => item.Key));
        Assert.Equal(expected.Length, actual.Select(static item => item.Key).Distinct().Count());
    }

    [Fact]
    public async Task ContinuationTokenBindsSemanticQueryButNotPageSize()
    {
        var fixture = CreateFixture();
        var day = new DateOnly(2026, 8, 29);
        await fixture.SeedAsync(
            QueryFixture.Projection(fixture.SourceA, day, "Availability", "1.0", 0.7m),
            QueryFixture.Projection(fixture.SourceA, day, "OEE", "1.0", 0.6m),
            QueryFixture.Projection(fixture.SourceB, day, "OEE", "1.0", 0.65m));
        var first = await fixture.Reader.ReadAsync(
            fixture.Query(day, day.AddDays(1), 1),
            CancellationToken.None);

        var continuation = await fixture.Reader.ReadAsync(
            fixture.Query(
                day,
                day.AddDays(1),
                2,
                first.ContinuationToken,
                sources: [fixture.SourceB, fixture.SourceA]),
            CancellationToken.None);

        Assert.Equal(2, continuation.Items.Count);
        Assert.Null(continuation.ContinuationToken);
    }

    [Fact]
    public async Task ContinuationTokenRejectsDifferentRangeOrderSourcesAndFilters()
    {
        var fixture = CreateFixture();
        var day = new DateOnly(2026, 8, 29);
        await fixture.SeedAsync(
            QueryFixture.Projection(fixture.SourceA, day, "Availability", "1.0", 0.7m),
            QueryFixture.Projection(fixture.SourceB, day, "OEE", "1.0", 0.65m));
        var first = await fixture.Reader.ReadAsync(
            fixture.Query(day, day.AddDays(1), 1),
            CancellationToken.None);
        var token = first.ContinuationToken;
        Assert.NotNull(token);

        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Reader.ReadAsync(
            fixture.Query(day.AddDays(-1), day.AddDays(1), 1, token),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Reader.ReadAsync(
            fixture.Query(
                day,
                day.AddDays(1),
                1,
                token,
                OperationalMetricReportOrder.PeriodDescending),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Reader.ReadAsync(
            fixture.Query(day, day.AddDays(1), 1, token, sources: [fixture.SourceA]),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Reader.ReadAsync(
            fixture.Query(
                day,
                day.AddDays(1),
                1,
                token,
                metrics: new OperationalMetricDefinitionSelection(
                    [new OperationalMetricDefinitionId("OEE", "1.0")])),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Reader.ReadAsync(
            fixture.Query(
                day,
                day.AddDays(1),
                1,
                token,
                context: new OperationalMetricContextFilter
                {
                    PartId = new PartId("part-1"),
                }),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Reader.ReadAsync(
            fixture.Query(
                day,
                day.AddDays(1),
                1,
                token,
                statuses: new OperationalMetricStatusSelection(
                    [OperationalMetricEvaluationStatus.Calculated])),
            CancellationToken.None));
    }

    [Fact]
    public async Task MalformedContinuationTokenFailsBeforeProviderRead()
    {
        var provider = new CountingProvider([]);
        var reader = new OperationalMetricReportingQueryReader(provider);
        var fixture = CreateFixture(reader);

        await Assert.ThrowsAsync<ArgumentException>(async () => await reader.ReadAsync(
            fixture.Query(
                new DateOnly(2026, 8, 29),
                new DateOnly(2026, 8, 30),
                10,
                new ReportingContinuationToken("not-a-cursor")),
            CancellationToken.None));

        Assert.Equal(0, provider.ReadCount);
    }

    [Fact]
    public async Task ContextStatusAndExactVersionFiltersUseCanonicalSemantics()
    {
        var fixture = CreateFixture();
        var day = new DateOnly(2026, 8, 29);
        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("order-1"),
            PartId = new PartId("part-1"),
            OperatorId = new OperatorId("operator-1"),
        };
        var insufficientContext = context with
        {
            OperatorId = new OperatorId("operator-2"),
        };
        await fixture.SeedAsync(
            QueryFixture.Projection(fixture.SourceA, day, "OEE", "1.0", 0.6m, context),
            QueryFixture.Projection(fixture.SourceA, day, "OEE", "2.0", 0.61m, context),
            QueryFixture.Projection(
                fixture.SourceA,
                day,
                "OEE",
                "1.0",
                null,
                insufficientContext,
                OperationalMetricEvaluationStatus.InsufficientEvidence),
            QueryFixture.Projection(fixture.SourceB, day, "OEE", "1.0", 0.7m));
        var query = fixture.Query(
            day,
            day.AddDays(1),
            20,
            metrics: new OperationalMetricDefinitionSelection(
                [new OperationalMetricDefinitionId("OEE", "1.0")]),
            context: new OperationalMetricContextFilter { PartId = new PartId("part-1") },
            statuses: new OperationalMetricStatusSelection(
                [OperationalMetricEvaluationStatus.Calculated]));

        var page = await fixture.Reader.ReadAsync(query, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("1.0", item.Key.DefinitionId.Version);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, item.Status);
        Assert.Equal(new PartId("part-1"), item.Key.ContextKey.PartId);
    }

    [Fact]
    public async Task InMemoryProviderUsesCanonicalGuidOrderingRatherThanInsertionOrder()
    {
        var machineA = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var machineB = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000100"));
        var fixture = CreateFixture(machineA, machineB);
        var day = new DateOnly(2026, 8, 29);
        var second = QueryFixture.Projection(fixture.SourceB, day, "OEE", "1.0", 0.7m);
        var first = QueryFixture.Projection(fixture.SourceA, day, "OEE", "1.0", 0.6m);
        await fixture.SeedAsync(second, first);

        var page = await fixture.Reader.ReadAsync(
            fixture.Query(day, day.AddDays(1), 10),
            CancellationToken.None);

        Assert.Equal([first.Key, second.Key], page.Items.Select(static item => item.Key));
    }

    [Fact]
    public async Task ShiftQueryUsesHalfOpenRangeAndCursorRoundTripsCompleteOccurrenceIdentity()
    {
        var fixture = CreateFixture();
        var rangeStart = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var before = QueryFixture.ShiftProjection(fixture.SourceA, rangeStart.AddHours(-8), "before");
        var first = QueryFixture.ShiftProjection(fixture.SourceA, rangeStart, "schedule-a");
        var tied = QueryFixture.ShiftProjection(fixture.SourceA, rangeStart, "schedule-b");
        var excludedEnd = QueryFixture.ShiftProjection(fixture.SourceA, rangeStart.AddHours(8), "at-end");
        await fixture.SeedAsync(before, first, tied, excludedEnd);
        var firstPage = await fixture.Reader.ReadAsync(
            fixture.ShiftQuery(rangeStart, rangeStart.AddHours(8), 1),
            CancellationToken.None);

        var firstItem = Assert.Single(firstPage.Items);
        Assert.Equal(first.Key, firstItem.Key);
        Assert.NotNull(firstPage.ContinuationToken);

        var secondPage = await fixture.Reader.ReadAsync(
            fixture.ShiftQuery(
                rangeStart,
                rangeStart.AddHours(8),
                1,
                firstPage.ContinuationToken),
            CancellationToken.None);

        Assert.Equal(tied.Key, Assert.Single(secondPage.Items).Key);
        Assert.Null(secondPage.ContinuationToken);
    }

    [Fact]
    public async Task ReaderRejectsProviderResultsOutsideQueryOrCanonicalOrder()
    {
        var fixture = CreateFixture();
        var day = new DateOnly(2026, 8, 29);
        var first = new OperationalMetricProjectionSummary(
            QueryFixture.Projection(fixture.SourceA, day, "Availability", "1.0", 0.7m));
        var second = new OperationalMetricProjectionSummary(
            QueryFixture.Projection(fixture.SourceA, day, "OEE", "1.0", 0.6m));
        var reader = new OperationalMetricReportingQueryReader(
            new CountingProvider([second, first]));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync(
            fixture.Query(day, day.AddDays(1), 10),
            CancellationToken.None));
    }

    [Fact]
    public async Task ReaderRejectsDuplicateEvaluationIdentityReturnedByProvider()
    {
        var fixture = CreateFixture();
        var day = new DateOnly(2026, 8, 29);
        var duplicate = new OperationalMetricProjectionSummary(
            QueryFixture.Projection(fixture.SourceA, day, "OEE", "1.0", 0.6m));
        var reader = new OperationalMetricReportingQueryReader(
            new CountingProvider([duplicate, duplicate]));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync(
            fixture.Query(day, day.AddDays(1), 10),
            CancellationToken.None));
    }

    [Fact]
    public async Task PreCancelledReadDoesNotInvokeProvider()
    {
        var provider = new CountingProvider([]);
        var reader = new OperationalMetricReportingQueryReader(provider);
        var fixture = CreateFixture(reader);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(
            fixture.Query(new DateOnly(2026, 8, 29), new DateOnly(2026, 8, 30), 10),
            cancellation.Token));

        Assert.Equal(0, provider.ReadCount);
    }

    private static QueryFixture CreateFixture(
        IOperationalMetricReportingQueryReader? reader = null) =>
        CreateFixture(
            new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            reader);

    private static QueryFixture CreateFixture(
        MachineId machineA,
        MachineId machineB,
        IOperationalMetricReportingQueryReader? reader = null)
    {
        var store = new InMemoryOperationalMetricProjectionStore();
        return new QueryFixture(
            new OperationalMetricReportingSource(
                machineA,
                new OperationalMetricProjectionProcessorId($"processor:{machineA.Value:D}")),
            new OperationalMetricReportingSource(
                machineB,
                new OperationalMetricProjectionProcessorId($"processor:{machineB.Value:D}")),
            store,
            reader ?? new OperationalMetricReportingQueryReader(store));
    }

    private sealed class CountingProvider(
        IReadOnlyList<OperationalMetricProjectionSummary> results) :
        IOperationalMetricReportingQueryProvider
    {
        public int ReadCount { get; private set; }

        public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadWindowAsync(
            OperationalMetricReportQuery query,
            OperationalMetricEvaluationKey? startAfter,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(results);
        }
    }

    private sealed record QueryFixture(
        OperationalMetricReportingSource SourceA,
        OperationalMetricReportingSource SourceB,
        InMemoryOperationalMetricProjectionStore Store,
        IOperationalMetricReportingQueryReader Reader)
    {
        public ProductionDayOperationalMetricReportQuery Query(
            DateOnly from,
            DateOnly to,
            int pageSize,
            ReportingContinuationToken? token = null,
            OperationalMetricReportOrder order = OperationalMetricReportOrder.PeriodAscending,
            IReadOnlyList<OperationalMetricReportingSource>? sources = null,
            OperationalMetricDefinitionSelection? metrics = null,
            OperationalMetricContextFilter? context = null,
            OperationalMetricStatusSelection? statuses = null) => new(
                new OperationalMetricReportingSourceSelection(sources ?? [SourceA, SourceB]),
                from,
                to,
                metrics,
                context,
                statuses,
                order,
                new ReportingPageRequest(pageSize, token));

        public ShiftOperationalMetricReportQuery ShiftQuery(
            DateTimeOffset from,
            DateTimeOffset to,
            int pageSize,
            ReportingContinuationToken? token = null) => new(
                new OperationalMetricReportingSourceSelection([SourceA, SourceB]),
                from,
                to,
                null,
                null,
                null,
                OperationalMetricReportOrder.PeriodAscending,
                new ReportingPageRequest(pageSize, token));

        public static OperationalMetricProjection Projection(
            OperationalMetricReportingSource source,
            DateOnly day,
            string metricKey,
            string version,
            decimal? value,
            OperationalMetricEvaluationContextKey? context = null,
            OperationalMetricEvaluationStatus status = OperationalMetricEvaluationStatus.Calculated)
        {
            var revision = Revision(source);
            return new OperationalMetricProjection(
                source.ProcessorId,
                new OperationalMetricEvaluationKey(
                    source.MachineId,
                    new OperationalMetricPeriodId.ProductionDay(
                        new ProductionDayId(new SiteId("site-a"), day)),
                    new OperationalMetricDefinitionId(metricKey, version),
                    context ?? OperationalMetricEvaluationContextKey.Unpartitioned),
                status,
                value,
                OperationalMetricUnits.Ratio,
                status == OperationalMetricEvaluationStatus.Calculated
                    ? null
                    : OperationalMetricEvaluationReasonCode.MissingOperand,
                status == OperationalMetricEvaluationStatus.Calculated ? null : "operand",
                revision);
        }

        public static OperationalMetricProjection ShiftProjection(
            OperationalMetricReportingSource source,
            DateTimeOffset startsAtUtc,
            string scheduleId)
        {
            var revision = Revision(source);
            return new OperationalMetricProjection(
                source.ProcessorId,
                new OperationalMetricEvaluationKey(
                    source.MachineId,
                    new OperationalMetricPeriodId.Shift(
                        new ShiftOccurrenceId(
                            new SiteId("site-a"),
                            new ShiftScheduleAssignmentId(scheduleId),
                            new ShiftId("shift-a"),
                            startsAtUtc,
                            startsAtUtc.AddHours(8))),
                    new OperationalMetricDefinitionId("OEE", "1.0"),
                    OperationalMetricEvaluationContextKey.Unpartitioned),
                OperationalMetricEvaluationStatus.Calculated,
                0.6m,
                OperationalMetricUnits.Ratio,
                null,
                null,
                revision);
        }

        public async Task SeedAsync(params OperationalMetricProjection[] projections)
        {
            foreach (var group in projections.GroupBy(static projection => projection.ProcessorId))
            {
                var snapshot = group.ToArray();
                var revision = snapshot[0].SourceRevision;
                await Store.CommitAsync(
                    new OperationalMetricProjectionCommit(
                        group.Key,
                        null,
                        new OperationalMetricProjectionCheckpoint(
                            group.Key,
                            revision,
                            new OperationalMetricProjectionBatchManifest(
                                snapshot.Select(static projection => projection.Key))),
                        snapshot),
                    CancellationToken.None);
            }
        }

        private static MetricAggregationCheckpoint Revision(
            OperationalMetricReportingSource source) => new(
                new MetricAggregationProcessorId($"aggregate:{source.MachineId.Value:D}"),
                MetricInputStreamId.ForMachine(source.MachineId),
                new MetricInputPosition(10));
    }
}
