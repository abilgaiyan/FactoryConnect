using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerEdgeProductionActivityHandoffIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly ObservationProcessorId StateProcessorId =
        new("machine-state-activity");

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerEdgeProductionActivityHandoffIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SqlOwnedActivityReaderDrivesProductionAndRestoresProgressAcrossRestart()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var activityStream = new ObservationStreamId(machineId, $"activity-{Guid.NewGuid():N}");
        var configuration = CreateConfiguration(_fixture.ConnectionString, machineId);
        await SeedObservationStreamCheckpointAsync(activityStream);

        ObservationProcessorId productionProcessorId;

        await using (var firstProvider = BuildProvider(configuration, activityStream))
        {
            var providerServices =
                firstProvider.GetRequiredService<PersistenceProviderServices>();
            var authorityStore = firstProvider.GetRequiredService<
                IMachineStateActivityAuthorityStore>();
            var activityReader = firstProvider.GetRequiredService<
                IProductionContextActivityReader>();

            Assert.IsType<SqlServerMachineStateActivityAuthorityStore>(authorityStore);
            Assert.IsType<SqlServerProductionContextActivityReader>(activityReader);
            Assert.Same(providerServices.MachineStateActivityAuthorityStore, authorityStore);
            Assert.Same(providerServices.ProductionContextActivityReader, activityReader);

            await PublishActivityAsync(
                authorityStore,
                activityStream,
                position: 1,
                instanceId: 101,
                state: MachineState.Running,
                startsAtUtc: Stamp,
                endsAtUtc: Stamp.AddMinutes(30),
                expectedPosition: null,
                expectedRevision: null);

            var runtimes = firstProvider.GetRequiredService<ProductionMetricInputRuntimeSet>();
            var activityRuntime = Assert.Single(runtimes.ActivityRuntimes);
            productionProcessorId = activityRuntime.ProcessorId;

            Assert.Equal(
                1,
                await activityRuntime.RunCycleAsync(CancellationToken.None));

            var checkpoint = await firstProvider
                .GetRequiredService<IProductionContextProcessingStore>()
                .ReadCheckpointAsync(
                    productionProcessorId,
                    activityStream,
                    CancellationToken.None);
            Assert.Equal(new ObservationPosition(1), checkpoint!.Position);

            var firstBatch = await firstProvider.GetRequiredService<IMetricInputReader>()
                .ReadAsync(
                    new MetricInputReadRequest(
                        MetricInputStreamId.ForMachine(machineId),
                        null,
                        100),
                    CancellationToken.None);
            Assert.Single(
                firstBatch.Facts,
                static item => item.Fact.Key == "duration.running");
        }

        await using (var restartedProvider = BuildProvider(configuration, activityStream))
        {
            var providerServices =
                restartedProvider.GetRequiredService<PersistenceProviderServices>();
            var authorityStore = restartedProvider.GetRequiredService<
                IMachineStateActivityAuthorityStore>();
            var activityReader = restartedProvider.GetRequiredService<
                IProductionContextActivityReader>();

            Assert.IsType<SqlServerMachineStateActivityAuthorityStore>(authorityStore);
            Assert.IsType<SqlServerProductionContextActivityReader>(activityReader);
            Assert.Same(providerServices.MachineStateActivityAuthorityStore, authorityStore);
            Assert.Same(providerServices.ProductionContextActivityReader, activityReader);

            var runtimes = restartedProvider.GetRequiredService<ProductionMetricInputRuntimeSet>();
            var activityRuntime = Assert.Single(runtimes.ActivityRuntimes);
            Assert.Equal(productionProcessorId, activityRuntime.ProcessorId);
            Assert.Equal(
                0,
                await activityRuntime.RunCycleAsync(CancellationToken.None));

            await PublishActivityAsync(
                authorityStore,
                activityStream,
                position: 2,
                instanceId: 102,
                state: MachineState.Idle,
                startsAtUtc: Stamp.AddMinutes(30),
                endsAtUtc: Stamp.AddHours(1),
                expectedPosition: new ObservationPosition(1),
                expectedRevision: new StateProjectionAuthorityRevision(0));

            Assert.Equal(
                1,
                await activityRuntime.RunCycleAsync(CancellationToken.None));

            var checkpoint = await restartedProvider
                .GetRequiredService<IProductionContextProcessingStore>()
                .ReadCheckpointAsync(
                    productionProcessorId,
                    activityStream,
                    CancellationToken.None);
            Assert.Equal(new ObservationPosition(2), checkpoint!.Position);

            var batch = await restartedProvider.GetRequiredService<IMetricInputReader>()
                .ReadAsync(
                    new MetricInputReadRequest(
                        MetricInputStreamId.ForMachine(machineId),
                        null,
                        100),
                    CancellationToken.None);
            Assert.Equal(
                2,
                batch.Facts.Count(item =>
                    item.Fact.Key is "duration.running" or "duration.idle"));
            Assert.Contains(
                batch.Facts,
                static item => item.Fact.Key == "duration.running");
            Assert.Contains(
                batch.Facts,
                static item => item.Fact.Key == "duration.idle");
        }
    }

    private ServiceProvider BuildProvider(
        IConfiguration configuration,
        ObservationStreamId activityStream)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(
            configuration,
            PersistenceProviderCapabilities.Core |
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);
        services.AddFactoryConnectObservationProcessing(configuration, activityStream);
        services.AddFactoryConnectProductionMetricInputs(configuration, activityStream);
        return services.BuildServiceProvider();
    }

    private async Task SeedObservationStreamCheckpointAsync(
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.ObservationStreamCheckpoint
                (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
            VALUES (@MachineId, @StreamKeyBinary, @StreamKey, 1, 1);
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            streamId.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        command.Parameters.Add("@StreamKey", SqlDbType.NVarChar, 256).Value =
            streamId.StreamKey;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task PublishActivityAsync(
        IMachineStateActivityAuthorityStore store,
        ObservationStreamId streamId,
        ulong position,
        ulong instanceId,
        MachineState state,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        ObservationPosition? expectedPosition,
        StateProjectionAuthorityRevision? expectedRevision)
    {
        var durablePosition = new ObservationPosition(position);
        var publication = new MachineStateActivityAuthorityPublication(
            expectedPosition,
            expectedRevision,
            new MachineStateActivityProjection(
                StateProcessorId,
                streamId,
                durablePosition,
                [],
                state,
                state,
                endsAtUtc),
            [],
            [
                new DurableMachineActivityPeriod(
                    StateProcessorId,
                    durablePosition,
                    streamId,
                    instanceId,
                    position,
                    new MachineActivityPeriod(
                        streamId.MachineId,
                        state,
                        startsAtUtc,
                        endsAtUtc))
            ],
            new EvaluationAuthorityReplayIdentity(
                StateProcessorId,
                streamId,
                durablePosition,
                state,
                instanceId,
                CanonicalCurrentStateContinuityPolicies.Preserve.Reference));

        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication, CancellationToken.None));
    }

    private static IConfiguration CreateConfiguration(
        string connectionString,
        MachineId machineId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "SqlServer",
                    [$"{SqlServerPersistenceOptions.SectionName}:ConnectionString"] =
                        connectionString,
                    ["ObservationProcessing:BatchSize"] = "100",
                    ["ObservationProcessing:PollingInterval"] = "00:00:01",
                    ["ObservationProcessing:Streams:0:MachineId"] = machineId.ToString(),
                    ["ObservationProcessing:Streams:0:StreamKey"] = "activity",
                    ["ObservationProcessing:Streams:0:Mappings:0:Source"] = "modbus",
                    ["ObservationProcessing:Streams:0:Mappings:0:Address"] = "DI1",
                    ["ObservationProcessing:Streams:0:Mappings:0:SignalKey"] =
                        CanonicalSignalKeys.Running,
                    ["ObservationProcessing:Streams:0:Mappings:0:Type"] = "Digital",
                    ["ProductionProcessing:BatchSize"] = "100",
                    ["ProductionProcessing:PollingInterval"] = "00:00:01",
                    ["ProductionProcessing:CompanyId"] = "COMP-1",
                    ["ProductionProcessing:SiteId"] = "SITE-1",
                    ["ProductionProcessing:ProductionLineId"] = "LINE-1",
                    ["ProductionProcessing:Contexts:0:AssignmentId"] = "CTX-1",
                    ["ProductionProcessing:Contexts:0:EffectiveFrom"] =
                        "2026-01-01T00:00:00+00:00",
                    ["ProductionProcessing:QuantityStreamKey"] = "production-quantity",
                    ["ProductionProcessing:ShiftSchedules:0:CompanyId"] = "COMP-1",
                    ["ProductionProcessing:ShiftSchedules:0:SiteId"] = "SITE-1",
                    ["ProductionProcessing:ShiftSchedules:0:ProductionLineId"] = "LINE-1",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:0"] = "Sunday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:1"] = "Monday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:2"] = "Tuesday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:3"] = "Wednesday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:4"] = "Thursday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:5"] = "Friday",
                    ["ProductionProcessing:ShiftSchedules:0:ActiveDays:6"] = "Saturday",
                    ["ProductionProcessing:ShiftSchedules:0:AssignmentId"] =
                        "SHIFT-SCHEDULE-1",
                    ["ProductionProcessing:ShiftSchedules:0:ShiftId"] = "SHIFT-1",
                    ["ProductionProcessing:ShiftSchedules:0:Name"] = "Shift 1",
                    ["ProductionProcessing:ShiftSchedules:0:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:ShiftSchedules:0:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:ShiftSchedules:0:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:ShiftSchedules:0:EffectiveFrom"] = "2026-01-01",
                    ["ProductionProcessing:PlannedProduction:AssignmentId"] = "POT-1",
                    ["ProductionProcessing:PlannedProduction:TimeZoneId"] = "UTC",
                    ["ProductionProcessing:PlannedProduction:StartsAtLocal"] = "06:00:00",
                    ["ProductionProcessing:PlannedProduction:EndsAtLocal"] = "14:00:00",
                    ["ProductionProcessing:PlannedProduction:EffectiveFrom"] = "2026-01-01",
                })
            .Build();

    private static readonly DateTimeOffset Stamp =
        new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
}
