using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryCurrentStateAuthorityCutProviderConformanceTests
{
    [Fact]
    public async Task StableAllNullCutIsAvailable()
    {
        var binding = Binding();
        var provider = Provider(binding, [null, null]);

        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));

        Assert.Equal(binding, result.Cut.Binding);
        Assert.Null(result.Cut.AcquisitionContact);
        Assert.Null(result.Cut.MappingCoverage);
        Assert.Null(result.Cut.Evaluation);
    }

    [Fact]
    public async Task StablePartialCutIsAvailable()
    {
        var binding = Binding();
        var mapping = Mapping(binding, raw: 10, mapped: 8, revision: 3);
        var provider = Provider(
            binding,
            [null, null],
            [mapping, mapping]);

        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));

        Assert.Null(result.Cut.AcquisitionContact);
        Assert.Equal(mapping, result.Cut.MappingCoverage);
        Assert.Null(result.Cut.Evaluation);
    }

    [Fact]
    public async Task StableCompleteConsistentCutIsAvailable()
    {
        var binding = Binding();
        var acquisition = Acquisition(binding, raw: 10, revision: 2);
        var mapping = Mapping(binding, raw: 10, mapped: 8, revision: 3);
        var evaluation = Snapshot(binding, position: 8, revision: 4);
        var provider = Provider(
            binding,
            [acquisition, acquisition],
            [mapping, mapping],
            [evaluation, evaluation]);

        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));

        Assert.Equal(acquisition, result.Cut.AcquisitionContact);
        Assert.Equal(mapping, result.Cut.MappingCoverage);
        Assert.Equal(evaluation.EvaluationAuthority, result.Cut.Evaluation);
    }

    [Fact]
    public async Task ImpossibleAcquisitionToMappingLineageIsUnavailable()
    {
        var binding = Binding();
        var acquisition = Acquisition(binding, raw: 5, revision: 0);
        var mapping = Mapping(binding, raw: 6, mapped: 5, revision: 0);
        var provider = Provider(
            binding,
            [acquisition, acquisition],
            [mapping, mapping]);

        Assert.Same(
            StableCurrentStateAuthorityCutUnavailable.Instance,
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task ImpossibleMappingToEvaluationLineageIsUnavailable()
    {
        var binding = Binding();
        var mapping = Mapping(binding, raw: 10, mapped: 7, revision: 0);
        var evaluation = Snapshot(binding, position: 8, revision: 0);
        var provider = Provider(
            binding,
            [null, null],
            [mapping, mapping],
            [evaluation, evaluation]);

        Assert.Same(
            StableCurrentStateAuthorityCutUnavailable.Instance,
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task OneUnstableAttemptRetriesImmediatelyAndReturnsNextStableCut()
    {
        var binding = Binding();
        var first = Acquisition(binding, raw: 5, revision: 0);
        var second = Acquisition(binding, raw: 6, revision: 1);
        var interleavings = 0;
        var provider = Provider(
            binding,
            [first, second, second, second],
            interleave: _ => interleavings++);

        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));

        Assert.Equal(second, result.Cut.AcquisitionContact);
        Assert.Equal(2, interleavings);
    }

    [Fact]
    public async Task ThreeUnstableAttemptsReturnUnavailableWithoutFourthAttempt()
    {
        var binding = Binding();
        var authorities = Enumerable.Range(0, 6)
            .Select(index => Acquisition(
                binding,
                raw: (ulong)index + 1,
                revision: (ulong)index))
            .ToArray();
        var source = new ScriptedObservationStore(authorities);
        var interleavings = 0;
        var provider = new InMemoryCurrentStateAuthorityCutProvider(
            source,
            new ScriptedMappingStore([null]),
            new ScriptedStateStore([null]),
            _ => interleavings++);

        Assert.Same(
            StableCurrentStateAuthorityCutUnavailable.Instance,
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));
        Assert.Equal(6, source.ReadCount);
        Assert.Equal(3, interleavings);
    }

    [Fact]
    public async Task CancellationBeforeAndBetweenCollectionsPropagates()
    {
        var binding = Binding();
        using var before = new CancellationTokenSource();
        before.Cancel();
        var provider = Provider(binding, [null]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ReadAuthorityCutAsync(binding, before.Token));

        using var between = new CancellationTokenSource();
        provider = Provider(
            binding,
            [null, null],
            interleave: _ => between.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ReadAuthorityCutAsync(binding, between.Token));
    }

    [Fact]
    public async Task ContradictoryReturnedIdentityIsUnavailable()
    {
        var binding = Binding();
        var other = Binding();
        var wrong = Acquisition(other, raw: 1, revision: 0);
        var provider = Provider(binding, [wrong, wrong]);

        Assert.Same(
            StableCurrentStateAuthorityCutUnavailable.Instance,
            await provider.ReadAuthorityCutAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryProviderOwnsCutProviderWithoutAdvertisingCapability()
    {
        ServiceCollection services = new();
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Persistence:Provider"] = "InMemory",
                    })
                .Build());

        using var serviceProvider = services.BuildServiceProvider();
        var providerServices =
            serviceProvider.GetRequiredService<PersistenceProviderServices>();
        var cutProvider = Assert.IsType<InMemoryCurrentStateAuthorityCutProvider>(
            providerServices.CurrentStateAuthorityCutProvider);

        Assert.Null(serviceProvider.GetService<ICurrentStateAuthorityCutProvider>());
        Assert.IsType<StableCurrentStateAuthorityCut>(
            await cutProvider.ReadAuthorityCutAsync(
                Binding(),
                CancellationToken.None));
        Assert.Equal(
            PersistenceProviderCapabilities.None,
            PersistenceProviderCapabilities.All &
                PersistenceProviderCapabilities.CurrentStateAuthorityReading);
    }

    private static InMemoryCurrentStateAuthorityCutProvider Provider(
        CurrentStateAuthorityBinding binding,
        IReadOnlyList<AcquisitionContactAuthority?> acquisitions,
        IReadOnlyList<MappingCoverageAuthority?>? mappings = null,
        IReadOnlyList<MachineStateActivityAuthoritySnapshot?>? evaluations = null,
        Action<int>? interleave = null) =>
        new(
            new ScriptedObservationStore(acquisitions),
            new ScriptedMappingStore(mappings ?? [null]),
            new ScriptedStateStore(evaluations ?? [null]),
            interleave);

    private static CurrentStateAuthorityBinding Binding()
    {
        var machine = MachineId.New();
        return new CurrentStateAuthorityBinding(
            machine,
            new ObservationStreamId(machine, "MTConnect:CNC-01"),
            new ObservationProcessorId("canonical-mapping"),
            new ObservationProcessorId("machine-state-activity"));
    }

    private static AcquisitionContactAuthority Acquisition(
        CurrentStateAuthorityBinding binding,
        ulong? raw,
        ulong revision) =>
        new(
            binding.ObservationStreamId,
            new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            raw is null ? null : new ObservationPosition(raw.Value),
            new AcquisitionAuthorityRevision(revision));

    private static MappingCoverageAuthority Mapping(
        CurrentStateAuthorityBinding binding,
        ulong raw,
        ulong? mapped,
        ulong revision) =>
        new(
            binding.MappingProcessorId,
            binding.ObservationStreamId,
            new ObservationPosition(raw),
            mapped is null ? null : new ObservationPosition(mapped.Value),
            new MappingAuthorityRevision(revision));

    private static MachineStateActivityAuthoritySnapshot Snapshot(
        CurrentStateAuthorityBinding binding,
        ulong position,
        ulong revision)
    {
        var durablePosition = new ObservationPosition(position);
        var policy = new CurrentStatePolicyReference("continuity/preserve", "1.0");
        var projection = new MachineStateActivityProjection(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            durablePosition,
            [],
            MachineState.Running,
            null,
            null);
        var authority = new EvaluationAuthority(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            durablePosition,
            MachineState.Running,
            1,
            policy,
            new StateProjectionAuthorityRevision(revision));
        return new MachineStateActivityAuthoritySnapshot(projection, authority);
    }

    private sealed class ScriptedObservationStore : IObservationIngestionStore
    {
        private readonly Queue<AcquisitionContactAuthority?> _values;
        private AcquisitionContactAuthority? _last;

        public ScriptedObservationStore(
            IEnumerable<AcquisitionContactAuthority?> values)
        {
            _values = new Queue<AcquisitionContactAuthority?>(values);
        }

        public int ReadCount { get; private set; }

        public ValueTask<AcquisitionContactAuthority?>
            ReadAcquisitionContactAuthorityAsync(
                ObservationStreamId streamId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_values.Count > 0)
            {
                _last = _values.Dequeue();
            }

            return ValueTask.FromResult(_last);
        }

        public ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CommitAsync(
            ObservationIngestionBatch batch,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedMappingStore : IMappingCoverageAuthorityStore
    {
        private readonly Queue<MappingCoverageAuthority?> _values;
        private MappingCoverageAuthority? _last;

        public ScriptedMappingStore(IEnumerable<MappingCoverageAuthority?> values)
        {
            _values = new Queue<MappingCoverageAuthority?>(values);
        }

        public ValueTask<MappingCoverageAuthority?> ReadAsync(
            ObservationProcessorId mappingProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_values.Count > 0)
            {
                _last = _values.Dequeue();
            }

            return ValueTask.FromResult(_last);
        }

        public ValueTask CommitAsync(
            MappingCoverageCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedStateStore : IMachineStateActivityAuthorityStore
    {
        private readonly Queue<MachineStateActivityAuthoritySnapshot?> _values;
        private MachineStateActivityAuthoritySnapshot? _last;

        public ScriptedStateStore(
            IEnumerable<MachineStateActivityAuthoritySnapshot?> values)
        {
            _values = new Queue<MachineStateActivityAuthoritySnapshot?>(values);
        }

        public ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(
            ObservationProcessorId stateProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_values.Count > 0)
            {
                _last = _values.Dequeue();
            }

            return ValueTask.FromResult(_last);
        }

        public ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(
            MachineStateActivityAuthorityPublication publication,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
