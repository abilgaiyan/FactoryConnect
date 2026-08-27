using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerEdgeProductionAggregationCompositionTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerEdgeProductionAggregationCompositionTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SelectedSqlProviderPublishesAggregatesAndRestoresComposedProgress()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var activityStream = new ObservationStreamId(machineId, "activity");
        var quantityStream = new ObservationStreamId(
            machineId,
            "production-quantity");
        var configuration = CreateConfiguration(_fixture.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddSingleton<InMemoryMachineStateActivityProjectionStore>();
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            activityStream);
        services.AddFactoryConnectMetricAggregation(
            configuration,
            [machineId]);

        await using var provider = services.BuildServiceProvider();
        Assert.IsType<SqlServerProductionContextProcessingStore>(
            provider.GetRequiredService<IProductionContextProcessingStore>());
        Assert.IsType<SqlServerMetricInputStore>(
            provider.GetRequiredService<IMetricInputReader>());
        Assert.IsType<SqlServerMetricAggregationStore>(
            provider.GetRequiredService<IMetricAggregationStore>());

        await SeedActivityAsync(
            provider.GetRequiredService<InMemoryMachineStateActivityProjectionStore>(),
            activityStream);
        provider.GetRequiredService<InMemoryProductionQuantityEvidenceReader>()
            .Add(CreateQuantity(machineId, quantityStream));

        var producers = provider.GetRequiredService<ProductionMetricInputRuntimeSet>();
        Assert.Equal(
            1,
            await producers.ActivityRuntimes[0].RunCycleAsync(
                CancellationToken.None));
        Assert.Equal(
            1,
            await producers.QuantityRuntimes[0].RunCycleAsync(
                CancellationToken.None));

        var inputReader = provider.GetRequiredService<IMetricInputReader>();
        var metricStream = MetricInputStreamId.ForMachine(machineId);
        var batch = await inputReader.ReadAsync(
            new MetricInputReadRequest(metricStream, null, 100),
            CancellationToken.None);
        Assert.Contains(
            batch.Facts,
            static item => item.Fact.Key == "duration.running");
        Assert.Contains(
            batch.Facts,
            static item => item.Fact.Key == "quantity.good");

        var aggregationSet = provider.GetRequiredService<
            MetricAggregationProcessingRuntimeSet>();
        Assert.True(await aggregationSet.RunCycleAsync(CancellationToken.None));
        var aggregationRuntime = Assert.Single(aggregationSet.Runtimes);
        var aggregationStore = provider.GetRequiredService<IMetricAggregationStore>();
        var checkpoint = await aggregationStore.ReadCheckpointAsync(
            aggregationRuntime.ProcessorId,
            metricStream,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(batch.ThroughPosition, checkpoint.Position);

        var restartedActivity = new ProductionContextProcessingRuntime(
            producers.ActivityRuntimes[0].ProcessorId,
            provider.GetRequiredService<IProductionContextActivityReader>(),
            provider.GetRequiredService<IProductionContextReader>(),
            provider.GetRequiredService<ShiftOccurrenceResolver>(),
            provider.GetRequiredService<PlannedProductionIntervalResolver>(),
            provider.GetRequiredService<IProductionContextProcessingStore>(),
            provider.GetRequiredService<ProductionContextProcessingScope>(),
            batchSize: 100);
        var restartedQuantity = new ProductionQuantityFactProcessingRuntime(
            producers.QuantityRuntimes[0].ProcessorId,
            provider.GetRequiredService<IProductionQuantityEvidenceReader>(),
            provider.GetRequiredService<ShiftOccurrenceResolver>(),
            provider.GetRequiredService<IProductionContextProcessingStore>(),
            quantityStream,
            batchSize: 100);
        var restartedAggregation = new MetricAggregationProcessingRuntime(
            aggregationRuntime.ProcessorId,
            inputReader,
            aggregationStore,
            metricStream,
            batchSize: 100);

        Assert.Equal(
            0,
            await restartedActivity.RunCycleAsync(CancellationToken.None));
        Assert.Equal(
            0,
            await restartedQuantity.RunCycleAsync(CancellationToken.None));
        Assert.Equal(
            0,
            await restartedAggregation.RunCycleAsync(CancellationToken.None));
    }

    private static DurableProductionQuantityEvidence CreateQuantity(
        MachineId machineId,
        ObservationStreamId streamId) =>
        new(
            new ObservationPosition(1),
            streamId,
            new ProductionQuantityEvidence
            {
                Id = new ProductionQuantityEvidenceId($"Q-{Guid.NewGuid():N}"),
                CompanyId = new CompanyId("COMP-1"),
                SiteId = new SiteId("SITE-1"),
                ProductionLineId = new ProductionLineId("LINE-1"),
                MachineId = machineId,
                ShiftId = new ShiftId("SHIFT-1"),
                ProductionContextAssignmentId =
                    new ProductionContextAssignmentId("CTX-1"),
                OccurredAtUtc = new DateTimeOffset(
                    2026,
                    8,
                    27,
                    10,
                    30,
                    0,
                    TimeSpan.Zero),
                GoodQuantity = 3,
            });

    private static async Task SeedActivityAsync(
        InMemoryMachineStateActivityProjectionStore store,
        ObservationStreamId streamId)
    {
        var processorId = new ObservationProcessorId("machine-state-activity");
        var position = new ObservationPosition(1);
        var projection = new MachineStateActivityProjection(
            processorId,
            streamId,
            position,
            [],
            MachineState.Running,
            null,
            null);
        var period = new DurableMachineActivityPeriod(
            processorId,
            position,
            streamId,
            1,
            1,
            new MachineActivityPeriod(
                streamId.MachineId,
                MachineState.Running,
                new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 27, 11, 0, 0, TimeSpan.Zero)));

        await store.CommitAsync(
            new MachineStateActivityProjectionCommit(
                null,
                projection,
                [],
                [period]),
            CancellationToken.None);
    }

    private static IConfiguration CreateConfiguration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "SqlServer",
                    [$"{SqlServerPersistenceOptions.SectionName}:ConnectionString"] =
                        connectionString,
                    ["ProductionProcessing:BatchSize"] = "100",
                    ["ProductionProcessing:PollingInterval"] = "00:00:01",
                    ["ProductionProcessing:CompanyId"] = "COMP-1",
                    ["ProductionProcessing:SiteId"] = "SITE-1",
                    ["ProductionProcessing:ProductionLineId"] = "LINE-1",
                    ["ProductionProcessing:ContextAssignmentId"] = "CTX-1",
                    ["ProductionProcessing:ContextEffectiveFromUtc"] =
                        "2026-01-01T00:00:00+00:00",
                    ["ProductionProcessing:QuantityStreamKey"] =
                        "production-quantity",
                    ["ProductionProcessing:Shift:AssignmentId"] =
                        "SHIFT-SCHEDULE-1",
                    ["ProductionProcessing:Shift:ShiftId"] = "SHIFT-1",
                    ["ProductionProcessing:Shift:Name"] = "Shift 1",
                    ["ProductionProcessing:Shift:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:Shift:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:Shift:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:Shift:EffectiveFrom"] = "2026-01-01",
                    ["ProductionProcessing:PlannedProduction:AssignmentId"] =
                        "POT-1",
                    ["ProductionProcessing:PlannedProduction:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:PlannedProduction:StartsAtLocal"] =
                        "06:00:00",
                    ["ProductionProcessing:PlannedProduction:EndsAtLocal"] =
                        "14:00:00",
                    ["ProductionProcessing:PlannedProduction:EffectiveFrom"] =
                        "2026-01-01",
                    ["MetricAggregation:BatchSize"] = "100",
                    ["MetricAggregation:PollingInterval"] = "00:00:01",
                })
            .Build();
}
