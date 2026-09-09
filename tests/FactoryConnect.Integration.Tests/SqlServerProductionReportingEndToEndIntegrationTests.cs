using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerProductionReportingEndToEndIntegrationTests
{
    [Fact]
    public async Task PersistedProductionReportingSurvivesSqlReportingRecomposition()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        var configuration = BuildConfiguration(database.ConnectionString);

        var machineId = new MachineId(Guid.NewGuid());
        var processorId = new OperationalMetricProjectionProcessorId(
            $"production-reporting-e2e-{Guid.NewGuid():N}");
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var productionDayId = new ProductionDayId(siteId, new DateOnly(2026, 9, 7));
        var occurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero));
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;

        MetricAggregationCheckpoint sourceRevision;

        await using (var firstProvider = BuildServiceProvider(configuration))
        {
            var startupGate = firstProvider.GetRequiredService<IPersistenceStartupGate>();
            await startupGate.EnsureReadyAsync(CancellationToken.None);

            sourceRevision = await CreateCommittedSourceRevisionAsync(
                database.ConnectionString,
                machineId);

            var projection = CreateCalculatedProjection(
                processorId,
                machineId,
                occurrence,
                context,
                sourceRevision,
                0.8m);
            await PublishAsync(
                database.ConnectionString,
                new OperationalMetricProjectionCommit(
                    processorId,
                    null,
                    new OperationalMetricProjectionCheckpoint(
                        processorId,
                        sourceRevision,
                        new OperationalMetricProjectionBatchManifest([projection.Key])),
                    [projection]));

            var rosterStore = firstProvider.GetRequiredService<IMachineShiftOccurrenceRosterStore>();
            await rosterStore.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    null,
                    new MachineShiftOccurrenceRoster(
                        machineId,
                        lineId,
                        productionDayId,
                        new MachineShiftOccurrenceRosterRevision(1),
                        [
                            new MachineShiftOccurrenceOwnership(
                                machineId,
                                lineId,
                                occurrence,
                                productionDayId),
                        ])),
                CancellationToken.None);

            Assert.Null(firstProvider.GetService<IOperationalMetricProjectionStore>());
        }

        await using var secondProvider = BuildServiceProvider(configuration);
        var restartedStartupGate = secondProvider.GetRequiredService<IPersistenceStartupGate>();
        await restartedStartupGate.EnsureReadyAsync(CancellationToken.None);

        Assert.Null(secondProvider.GetService<IOperationalMetricProjectionStore>());

        var reader = secondProvider.GetRequiredService<IProductionDayShiftOperationalMetricQueryReader>();
        var reportingSource = new OperationalMetricReportingSource(machineId, processorId);
        var page = await reader.ReadAsync(
            new ProductionDayShiftOperationalMetricPageQuery(
                new ProductionDayShiftOperationalMetricQuery(
                    [new ProductionDayShiftReportingSource(reportingSource, productionDayId)],
                    context),
                new ReportingPageRequest(10)),
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Null(page.ContinuationToken);
        Assert.Equal(machineId, item.Source.MachineId);
        Assert.Equal(processorId, item.Source.ProcessorId);
        Assert.Equal(productionDayId, item.ProductionDayId);
        Assert.Equal(lineId, item.ProductionLineId);
        Assert.Equal(occurrence, item.ShiftOccurrenceId);
        Assert.Equal(context, item.ContextKey);
        Assert.NotNull(item.SourceRevision);
        Assert.Equal(sourceRevision, item.SourceRevision);

        var metric = Assert.Single(item.Metrics);
        Assert.Equal(
            new OperationalMetricDefinitionId("availability", "1"),
            metric.DefinitionId);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, metric.Status);
        Assert.Equal(0.8m, metric.Value);
    }

    private static ServiceProvider BuildServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(
            configuration,
            PersistenceProviderCapabilities.OperationalMetricProjectionQuery |
            PersistenceProviderCapabilities.OperationalMetricReportingQuery |
            PersistenceProviderCapabilities.MachineShiftOccurrenceRoster);
        services.AddFactoryConnectOperationalMetricReporting();
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] =
                    SqlServerPersistenceServiceCollectionExtensions.ProviderKey,
                ["PersistenceProviders:SqlServer:ConnectionString"] = connectionString,
            })
            .Build();

    private static async Task<MetricAggregationCheckpoint> CreateCommittedSourceRevisionAsync(
        string connectionString,
        MachineId machineId)
    {
        var inputStore = new SqlServerMetricInputStore(connectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"production-reporting-e2e-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"production-reporting-e2e-aggregation-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(connectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, null, checkpoint, []),
            CancellationToken.None);
        return checkpoint;
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

    private static OperationalMetricProjection CreateCalculatedProjection(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ShiftOccurrenceId occurrence,
        OperationalMetricEvaluationContextKey context,
        MetricAggregationCheckpoint sourceRevision,
        decimal value) =>
        new(
            processorId,
            new OperationalMetricEvaluationKey(
                machineId,
                new OperationalMetricPeriodId.Shift(occurrence),
                new OperationalMetricDefinitionId("availability", "1"),
                context),
            OperationalMetricEvaluationStatus.Calculated,
            value,
            "ratio",
            null,
            null,
            sourceRevision);

    private static async Task PublishAsync(
        string connectionString,
        OperationalMetricProjectionCommit commit)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(connectionString);
        await transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    cancellationToken),
            CancellationToken.None);
    }
}
