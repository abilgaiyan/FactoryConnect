using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Testing;

public interface IOperationalMetricReportingQueryProviderFixture : IAsyncDisposable
{
    IOperationalMetricReportingQueryProvider Provider { get; }

    Task SeedAsync(params OperationalMetricProjection[] projections);
}

public abstract class OperationalMetricReportingQueryProviderConformanceTests
{
    private static readonly DateOnly FirstDay = new(2026, 8, 29);

    protected abstract ValueTask<IOperationalMetricReportingQueryProviderFixture>
        CreateProviderAsync();

    [Fact]
    public async Task TraversesAscendingCanonicalOrderAcrossPages()
    {
        await using var fixture = await CreateProviderAsync();
        var sources = Sources();
        var expected = new[]
        {
            Projection(sources[0], FirstDay, "Availability", "1.0"),
            Projection(sources[0], FirstDay, "Availability", "2.0"),
            Projection(sources[0], FirstDay, "OEE", "1.0"),
            Projection(sources[1], FirstDay, "Availability", "1.0"),
            Projection(sources[0], FirstDay.AddDays(1), "Availability", "1.0"),
        };
        await fixture.SeedAsync(expected);

        var actual = await TraverseAsync(
            fixture.Provider,
            sources,
            OperationalMetricReportOrder.PeriodAscending,
            pageSize: 1);

        Assert.Equal(
            expected.Select(static projection => projection.Key),
            actual.Select(static summary => summary.Key));
    }

    [Fact]
    public async Task TraversesDescendingCanonicalOrderAcrossPages()
    {
        await using var fixture = await CreateProviderAsync();
        var sources = Sources();
        var earlierA = Projection(sources[0], FirstDay, "Availability", "1.0");
        var earlierB = Projection(sources[1], FirstDay, "Availability", "1.0");
        var laterA = Projection(sources[0], FirstDay.AddDays(1), "Availability", "1.0");
        var latestA = Projection(sources[0], FirstDay.AddDays(2), "Availability", "1.0");
        await fixture.SeedAsync(earlierB, latestA, earlierA, laterA);

        var actual = await TraverseAsync(
            fixture.Provider,
            sources,
            OperationalMetricReportOrder.PeriodDescending,
            pageSize: 1);

        Assert.Equal(
            [latestA.Key, laterA.Key, earlierA.Key, earlierB.Key],
            actual.Select(static summary => summary.Key));
    }

    [Fact]
    public async Task OrdersSameProductionDateByCompleteSiteIdentityAcrossPages()
    {
        await using var fixture = await CreateProviderAsync();
        var source = Sources()[0];
        var siteB = Projection(source, FirstDay, "OEE", "1.0", siteId: "site-b");
        var siteA = Projection(source, FirstDay, "OEE", "1.0", siteId: "site-a");
        await fixture.SeedAsync(siteB, siteA);

        var actual = await TraverseAsync(
            fixture.Provider,
            [source],
            OperationalMetricReportOrder.PeriodAscending,
            pageSize: 1);

        Assert.Equal(
            [siteA.Key, siteB.Key],
            actual.Select(static summary => summary.Key));
    }

    [Fact]
    public async Task OrdersUnpartitionedBeforePartitionedContextsAcrossPages()
    {
        await using var fixture = await CreateProviderAsync();
        var source = Sources()[0];
        var partitioned = Projection(
            source,
            FirstDay,
            "OEE",
            "1.0",
            context: new OperationalMetricEvaluationContextKey
            {
                ProductionOrderId = new ProductionOrderId("order-1"),
                PartId = new PartId("part-1"),
            });
        var unpartitioned = Projection(source, FirstDay, "OEE", "1.0");
        await fixture.SeedAsync(partitioned, unpartitioned);

        var actual = await TraverseAsync(
            fixture.Provider,
            [source],
            OperationalMetricReportOrder.PeriodAscending,
            pageSize: 1);

        Assert.Equal(
            [unpartitioned.Key, partitioned.Key],
            actual.Select(static summary => summary.Key));
    }

    [Fact]
    public async Task ReorderedMetricAndStatusSelectionsAcceptSameCursor()
    {
        await using var fixture = await CreateProviderAsync();
        var source = Sources()[0];
        var availability = new OperationalMetricDefinitionId("Availability", "1.0");
        var oee = new OperationalMetricDefinitionId("OEE", "1.0");
        await fixture.SeedAsync(
            Projection(source, FirstDay, availability.MetricKey, availability.Version),
            Projection(source, FirstDay, oee.MetricKey, oee.Version),
            Projection(
                source,
                FirstDay.AddDays(1),
                oee.MetricKey,
                oee.Version,
                status: OperationalMetricEvaluationStatus.InsufficientEvidence));
        var reader = new OperationalMetricReportingQueryReader(fixture.Provider);
        var firstQuery = Query(
            [source],
            OperationalMetricReportOrder.PeriodAscending,
            pageSize: 1,
            metrics: new OperationalMetricDefinitionSelection([oee, availability]),
            statuses: new OperationalMetricStatusSelection(
            [
                OperationalMetricEvaluationStatus.InsufficientEvidence,
                OperationalMetricEvaluationStatus.Calculated,
            ]));
        var firstPage = await reader.ReadAsync(firstQuery, CancellationToken.None);

        var secondPage = await reader.ReadAsync(
            Query(
                [source],
                OperationalMetricReportOrder.PeriodAscending,
                pageSize: 2,
                token: firstPage.ContinuationToken,
                metrics: new OperationalMetricDefinitionSelection([availability, oee]),
                statuses: new OperationalMetricStatusSelection(
                [
                    OperationalMetricEvaluationStatus.Calculated,
                    OperationalMetricEvaluationStatus.InsufficientEvidence,
                ])),
            CancellationToken.None);

        Assert.Equal(2, secondPage.Items.Count);
        Assert.Null(secondPage.ContinuationToken);
    }

    [Fact]
    public async Task UsesDotNetGuidOrderForSqlServerDivergentPair()
    {
        await using var fixture = await CreateProviderAsync();
        // SQL Server gives the trailing six bytes higher ordering significance for
        // uniqueidentifier values, while Guid.CompareTo starts with the first component.
        var dotNetFirst = new OperationalMetricReportingSource(
            new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new OperationalMetricProjectionProcessorId("processor-dotnet-first"));
        var sqlServerFirst = new OperationalMetricReportingSource(
            new MachineId(Guid.Parse("01000000-0000-0000-0000-000000000000")),
            new OperationalMetricProjectionProcessorId("processor-sql-first"));
        Assert.True(dotNetFirst.MachineId.Value.CompareTo(sqlServerFirst.MachineId.Value) < 0);
        var expectedFirst = Projection(dotNetFirst, FirstDay, "OEE", "1.0");
        var expectedSecond = Projection(sqlServerFirst, FirstDay, "OEE", "1.0");
        await fixture.SeedAsync(expectedSecond, expectedFirst);

        var actual = await TraverseAsync(
            fixture.Provider,
            [sqlServerFirst, dotNetFirst],
            OperationalMetricReportOrder.PeriodAscending,
            pageSize: 1);

        Assert.Equal(
            [expectedFirst.Key, expectedSecond.Key],
            actual.Select(static summary => summary.Key));
    }

    [Fact]
    public async Task ProviderPropagatesPreCancelledWindowRead()
    {
        await using var fixture = await CreateProviderAsync();
        var source = Sources()[0];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Provider.ReadWindowAsync(
                Query(
                    [source],
                    OperationalMetricReportOrder.PeriodAscending,
                    pageSize: 10),
                null,
                11,
                cancellation.Token));
    }

    [Fact]
    public async Task CursorSeeksAfterMissingPreviousRow()
    {
        var sources = Sources();
        var first = Projection(sources[0], FirstDay, "Availability", "1.0");
        var second = Projection(sources[0], FirstDay, "OEE", "1.0");
        var third = Projection(sources[1], FirstDay, "OEE", "1.0");
        ReportingContinuationToken token;

        await using (var original = await CreateProviderAsync())
        {
            await original.SeedAsync(first, second, third);
            var page = await new OperationalMetricReportingQueryReader(original.Provider).ReadAsync(
                Query(
                    sources,
                    OperationalMetricReportOrder.PeriodAscending,
                    pageSize: 1),
                CancellationToken.None);
            token = Assert.IsType<ReportingContinuationToken>(page.ContinuationToken);
        }

        await using var replacement = await CreateProviderAsync();
        await replacement.SeedAsync(second, third);
        var continued = await new OperationalMetricReportingQueryReader(replacement.Provider).ReadAsync(
            Query(
                sources,
                OperationalMetricReportOrder.PeriodAscending,
                pageSize: 10,
                token: token),
            CancellationToken.None);

        Assert.Equal(
            [second.Key, third.Key],
            continued.Items.Select(static summary => summary.Key));
    }

    private static async Task<IReadOnlyList<OperationalMetricProjectionSummary>> TraverseAsync(
        IOperationalMetricReportingQueryProvider provider,
        IReadOnlyList<OperationalMetricReportingSource> sources,
        OperationalMetricReportOrder order,
        int pageSize)
    {
        var reader = new OperationalMetricReportingQueryReader(provider);
        var result = new List<OperationalMetricProjectionSummary>();
        ReportingContinuationToken? token = null;
        do
        {
            var page = await reader.ReadAsync(
                Query(sources, order, pageSize, token),
                CancellationToken.None);
            result.AddRange(page.Items);
            token = page.ContinuationToken;
        }
        while (token is not null);

        return result;
    }

    private static ProductionDayOperationalMetricReportQuery Query(
        IReadOnlyList<OperationalMetricReportingSource> sources,
        OperationalMetricReportOrder order,
        int pageSize,
        ReportingContinuationToken? token = null,
        OperationalMetricDefinitionSelection? metrics = null,
        OperationalMetricStatusSelection? statuses = null) => new(
            new OperationalMetricReportingSourceSelection(sources),
            FirstDay,
            FirstDay.AddDays(4),
            metrics,
            null,
            statuses,
            order,
            new ReportingPageRequest(pageSize, token));

    private static OperationalMetricReportingSource[] Sources() =>
    [
        new(
            new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new OperationalMetricProjectionProcessorId("processor-a")),
        new(
            new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new OperationalMetricProjectionProcessorId("processor-b")),
    ];

    private static OperationalMetricProjection Projection(
        OperationalMetricReportingSource source,
        DateOnly day,
        string metricKey,
        string version,
        decimal? value = 0.6m,
        string siteId = "site-a",
        OperationalMetricEvaluationContextKey? context = null,
        OperationalMetricEvaluationStatus status = OperationalMetricEvaluationStatus.Calculated)
    {
        var revision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId($"aggregate:{source.MachineId.Value:D}"),
            MetricInputStreamId.ForMachine(source.MachineId),
            new MetricInputPosition(10));
        return new OperationalMetricProjection(
            source.ProcessorId,
            new OperationalMetricEvaluationKey(
                source.MachineId,
                new OperationalMetricPeriodId.ProductionDay(
                    new ProductionDayId(new SiteId(siteId), day)),
                new OperationalMetricDefinitionId(metricKey, version),
                context ?? OperationalMetricEvaluationContextKey.Unpartitioned),
            status,
            status == OperationalMetricEvaluationStatus.Calculated ? value : null,
            OperationalMetricUnits.Ratio,
            status == OperationalMetricEvaluationStatus.Calculated
                ? null
                : OperationalMetricEvaluationReasonCode.MissingOperand,
            status == OperationalMetricEvaluationStatus.Calculated ? null : "operand",
            revision);
    }
}
