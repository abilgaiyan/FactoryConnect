using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerCurrentStateComposedConformanceTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerCurrentStateComposedConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void C01ComposedGraphUsesExactSqlProviderOwnedCurrentStateServices()
    {
        var machineId = MachineId.New();
        using var provider = CreateProvider(machineId);

        var providerServices =
            provider.GetRequiredService<PersistenceProviderServices>();

        Assert.IsType<SqlServerMappingCoverageAuthorityStore>(
            providerServices.MappingCoverageAuthorityStore);
        Assert.IsType<SqlServerMachineStateActivityAuthorityStore>(
            providerServices.MachineStateActivityAuthorityStore);
        Assert.IsType<SqlServerCurrentStateAuthorityCutProvider>(
            providerServices.CurrentStateAuthorityCutProvider);

        Assert.Same(
            providerServices.MappingCoverageAuthorityStore,
            provider.GetRequiredService<IMappingCoverageAuthorityStore>());
        Assert.Same(
            providerServices.MachineStateActivityAuthorityStore,
            provider.GetRequiredService<IMachineStateActivityAuthorityStore>());
        Assert.Same(
            providerServices.CurrentStateAuthorityCutProvider,
            provider.GetRequiredService<ICurrentStateAuthorityCutProvider>());

        Assert.NotNull(provider.GetRequiredService<DurableObservationProcessingPipeline>());
        Assert.NotNull(provider.GetRequiredService<ICurrentMachineStateReader>());
    }

    [Fact]
    public async Task C02DurableObservationFlowsThroughSqlAuthoritiesToSemanticEvidence()
    {
        var machineId = MachineId.New();
        using var provider = CreateProvider(machineId);

        await CommitRunningObservationAsync(provider, machineId);
        await provider.GetRequiredService<DurableObservationProcessingPipeline>()
            .RunCycleAsync();

        var result = await provider.GetRequiredService<ICurrentMachineStateReader>()
            .ReadAsync(machineId, CancellationToken.None);
        var evidence = Assert.IsType<CurrentMachineStateEvidence>(result);

        Assert.Equal(machineId, evidence.MachineId);
        Assert.Equal(MachineState.Running, evidence.MachineState);
        Assert.Equal(CurrentStateCoverage.Complete, evidence.Coverage);
        Assert.Equal(CurrentStateFreshness.Current, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Current, evidence.Usability);

        var cut = await ReadStableCutAsync(provider, machineId);
        Assert.Equal(new ObservationPosition(1), cut.AcquisitionContact!.RawAcceptedThrough);
        Assert.Equal(new ObservationPosition(1), cut.MappingCoverage!.RawConsumedThrough);
        Assert.Equal(new ObservationPosition(1), cut.MappingCoverage.MappedEvaluationInputHighWater);
        Assert.Equal(new ObservationPosition(1), cut.Evaluation!.EvaluatedThrough);
        Assert.Equal(MachineState.Running, cut.Evaluation.MachineState);
    }

    [Fact]
    public async Task C03ReconstructedCompositionReadsPersistedSqlCurrentState()
    {
        var machineId = MachineId.New();

        using (var first = CreateProvider(machineId))
        {
            await CommitRunningObservationAsync(first, machineId);
            await first.GetRequiredService<DurableObservationProcessingPipeline>()
                .RunCycleAsync();

            var before = Assert.IsType<CurrentMachineStateEvidence>(
                await first.GetRequiredService<ICurrentMachineStateReader>()
                    .ReadAsync(machineId, CancellationToken.None));
            Assert.Equal(MachineState.Running, before.MachineState);
        }

        using var restarted = CreateProvider(machineId);
        var after = Assert.IsType<CurrentMachineStateEvidence>(
            await restarted.GetRequiredService<ICurrentMachineStateReader>()
                .ReadAsync(machineId, CancellationToken.None));

        Assert.Equal(MachineState.Running, after.MachineState);
        Assert.Equal(CurrentStateCoverage.Complete, after.Coverage);
        Assert.Equal(CurrentStateFreshness.Current, after.Freshness);
        Assert.Equal(CurrentStateUsability.Current, after.Usability);

        var cut = await ReadStableCutAsync(restarted, machineId);
        Assert.Equal(new ObservationPosition(1), cut.MappingCoverage!.RawConsumedThrough);
        Assert.Equal(new ObservationPosition(1), cut.Evaluation!.EvaluatedThrough);
        Assert.Equal(0UL, cut.Evaluation.ProjectionRevision.Value);
    }

    [Fact]
    public async Task C04RestartContinuesForwardFromPersistedSqlAuthority()
    {
        var machineId = MachineId.New();

        using (var first = CreateProvider(machineId))
        {
            await CommitRunningObservationAsync(first, machineId);
            await first.GetRequiredService<DurableObservationProcessingPipeline>()
                .RunCycleAsync();
        }

        using var restarted = CreateProvider(machineId);
        await CommitStoppedObservationAsync(restarted, machineId);

        var advanced = await restarted
            .GetRequiredService<DurableObservationProcessingPipeline>()
            .RunCycleAsync();
        Assert.True(advanced);

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await restarted.GetRequiredService<ICurrentMachineStateReader>()
                .ReadAsync(machineId, CancellationToken.None));

        // Running=false is deliberately mapped to the canonical Running signal.
        // With the persisted prior Running=true signal replaced by false, the
        // evaluator deterministically produces Stopped.
        Assert.Equal(MachineState.Stopped, evidence.MachineState);
        Assert.Equal(CurrentStateUsability.Current, evidence.Usability);

        var cut = await ReadStableCutAsync(restarted, machineId);
        Assert.Equal(new ObservationPosition(2), cut.AcquisitionContact!.RawAcceptedThrough);
        Assert.Equal(new ObservationPosition(2), cut.MappingCoverage!.RawConsumedThrough);
        Assert.Equal(new ObservationPosition(2), cut.MappingCoverage.MappedEvaluationInputHighWater);
        Assert.Equal(new ObservationPosition(2), cut.Evaluation!.EvaluatedThrough);
        Assert.Equal(1UL, cut.Evaluation.ProjectionRevision.Value);
        Assert.Equal(MachineState.Stopped, cut.Evaluation.MachineState);
    }

    [Fact]
    public async Task C05RestartAndForwardContinuationDoNotDuplicateDurableHistory()
    {
        var machineId = MachineId.New();

        using (var first = CreateProvider(machineId))
        {
            await CommitRunningObservationAsync(first, machineId);
            await first.GetRequiredService<DurableObservationProcessingPipeline>()
                .RunCycleAsync();
        }

        Assert.Equal(1, await CountHistoryAsync("dbo.MachineStateChangeHistory", machineId));
        Assert.Equal(0, await CountHistoryAsync("dbo.MachineActivityPeriodHistory", machineId));

        using (var restarted = CreateProvider(machineId))
        {
            // A restart cycle with no new durable observation must remain a no-op.
            Assert.False(await restarted
                .GetRequiredService<DurableObservationProcessingPipeline>()
                .RunCycleAsync());

            Assert.Equal(1, await CountHistoryAsync("dbo.MachineStateChangeHistory", machineId));
            Assert.Equal(0, await CountHistoryAsync("dbo.MachineActivityPeriodHistory", machineId));

            await CommitStoppedObservationAsync(restarted, machineId);
            await restarted.GetRequiredService<DurableObservationProcessingPipeline>()
                .RunCycleAsync();

            var cut = await ReadStableCutAsync(restarted, machineId);
            Assert.Equal(1UL, cut.Evaluation!.ProjectionRevision.Value);
            Assert.Equal(new ObservationPosition(2), cut.Evaluation.EvaluatedThrough);
        }

        Assert.Equal(2, await CountHistoryAsync("dbo.MachineStateChangeHistory", machineId));
        Assert.Equal(1, await CountHistoryAsync("dbo.MachineActivityPeriodHistory", machineId));
    }

    private ServiceProvider CreateProvider(MachineId machineId)
    {
        var configuration = Configuration(_fixture.ConnectionString);
        var inventory = Inventory(machineId);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(Stamp.AddSeconds(30)));
        services.AddFactoryConnectEdgePersistence(
            configuration,
            PersistenceProviderCapabilities.All |
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            inventory.ActivityStreams);
        services.AddFactoryConnectCurrentMachineState(
            configuration,
            inventory);

        return services.BuildServiceProvider();
    }

    private static async Task CommitRunningObservationAsync(
        IServiceProvider provider,
        MachineId machineId)
    {
        var store = provider.GetRequiredService<IObservationIngestionStore>();
        var streamId = Stream(machineId);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                expectedCheckpoint: null,
                new ObservationCheckpoint(streamId, instanceId: 1, nextSequence: 2),
                [
                    new SequencedMachineObservation(
                        sequence: 1,
                        Observation(machineId, running: true, Stamp)),
                ],
                successfulContactTime: Stamp));
    }

    private static async Task CommitStoppedObservationAsync(
        IServiceProvider provider,
        MachineId machineId)
    {
        var store = provider.GetRequiredService<IObservationIngestionStore>();
        var streamId = Stream(machineId);
        var expected = await store.ReadCheckpointAsync(streamId);

        Assert.NotNull(expected);
        Assert.Equal(1UL, expected.InstanceId);
        Assert.Equal(2UL, expected.NextSequence);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                expected,
                new ObservationCheckpoint(streamId, instanceId: 1, nextSequence: 3),
                [
                    new SequencedMachineObservation(
                        sequence: 2,
                        Observation(machineId, running: false, Stamp.AddSeconds(10))),
                ],
                successfulContactTime: Stamp.AddSeconds(10)));
    }

    private static MachineObservation Observation(
        MachineId machineId,
        bool running,
        DateTimeOffset timestamp) =>
        new()
        {
            MachineId = machineId,
            Source = "modbus",
            Address = "DI1",
            Type = SignalType.Digital,
            Value = running,
            Quality = ObservationQuality.Good,
            Timestamp = timestamp,
        };

    private static async Task<CurrentStateAuthorityCut> ReadStableCutAsync(
        IServiceProvider provider,
        MachineId machineId)
    {
        var resolution = provider.GetRequiredService<ICurrentStateOwnerResolver>()
            .Resolve(machineId);
        var owner = Assert.IsType<CurrentStateExactlyOneOwner>(resolution);
        var result = await provider
            .GetRequiredService<ICurrentStateAuthorityCutProvider>()
            .ReadAuthorityCutAsync(owner.Binding, CancellationToken.None);

        return Assert.IsType<StableCurrentStateAuthorityCut>(result).Cut;
    }

    private async Task<int> CountHistoryAsync(string table, MachineId machineId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT_BIG(1) FROM {table} WHERE MachineId = @MachineId;";
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            machineId.Value;

        return checked((int)Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ObservationStreamId Stream(MachineId machineId) =>
        MtConnectObservationStreamId.Create(machineId, "CNC-01");

    private static MtConnectMachineInventory Inventory(MachineId machineId) =>
        new(
            [
                new MtConnectAcquisitionOptions(
                    new MtConnectEndpoint(new Uri("http://localhost:5001/")),
                    machineId,
                    "CNC-01",
                    fromSequence: 1,
                    pollingInterval: TimeSpan.FromSeconds(1)),
            ]);

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "SqlServer",
                    ["PersistenceProviders:SqlServer:ConnectionString"] = connectionString,
                    ["ObservationProcessing:BatchSize"] = "10",
                    ["ObservationProcessing:PollingInterval"] = "00:00:01",
                    ["ObservationProcessing:Mappings:0:Source"] = "modbus",
                    ["ObservationProcessing:Mappings:0:Address"] = "DI1",
                    ["ObservationProcessing:Mappings:0:SignalKey"] =
                        CanonicalSignalKeys.Running,
                    ["ObservationProcessing:Mappings:0:Type"] = "Digital",
                    ["ObservationProcessing:Mappings:0:Invert"] = "false",
                    ["CurrentState:Freshness:MaximumCurrentAge"] = "00:01:00",
                })
            .Build();

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
