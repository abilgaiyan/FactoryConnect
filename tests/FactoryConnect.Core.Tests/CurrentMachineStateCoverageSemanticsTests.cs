using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class CurrentMachineStateCoverageSemanticsTests
{
    [Fact]
    public void MissingMappingIsIndeterminateRegardlessOfAcquisition()
    {
        Assert.Equal(
            CurrentStateCoverage.Indeterminate,
            CurrentMachineStateReader.ClassifyCoverage(null, null));
    }

    [Fact]
    public void EstablishedEmptyMappedFrontierIsComplete()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 5, mappedHighWater: null);

        Assert.Equal(
            CurrentStateCoverage.Complete,
            CurrentMachineStateReader.ClassifyCoverage(mapping, null));
    }

    [Fact]
    public void MappedInputWithoutEvaluationIsBehind()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 5, mappedHighWater: 4);

        Assert.Equal(
            CurrentStateCoverage.Behind,
            CurrentMachineStateReader.ClassifyCoverage(mapping, null));
    }

    [Fact]
    public void EvaluationTrailingMappedFrontierIsBehind()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 8, mappedHighWater: 7);
        var evaluation = CreateEvaluation(binding, evaluatedThrough: 6);

        Assert.Equal(
            CurrentStateCoverage.Behind,
            CurrentMachineStateReader.ClassifyCoverage(mapping, evaluation));
    }

    [Fact]
    public void EvaluationAtMappedFrontierIsComplete()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 8, mappedHighWater: 7);
        var evaluation = CreateEvaluation(binding, evaluatedThrough: 7);

        Assert.Equal(
            CurrentStateCoverage.Complete,
            CurrentMachineStateReader.ClassifyCoverage(mapping, evaluation));
    }

    [Fact]
    public void EvaluationWithoutMappingIsIndeterminate()
    {
        var binding = CreateBinding();
        var evaluation = CreateEvaluation(binding, evaluatedThrough: 3);

        Assert.Equal(
            CurrentStateCoverage.Indeterminate,
            CurrentMachineStateReader.ClassifyCoverage(null, evaluation));
    }

    [Fact]
    public async Task EmptyMappedFrontierWithoutEvaluationReturnsCompleteNoEvidence()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 5, mappedHighWater: null);
        var reader = CreateReader(binding, new CurrentStateAuthorityCut(binding, null, mapping, null));

        var result = await reader.ReadAsync(binding.MachineId, CancellationToken.None);

        var noEvidence = Assert.IsType<CurrentMachineStateNoEvidence>(result);
        Assert.Equal(CurrentStateCoverage.Complete, noEvidence.Coverage);
    }

    [Fact]
    public async Task MappedInputWithoutEvaluationReturnsBehindNoEvidence()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 5, mappedHighWater: 4);
        var reader = CreateReader(binding, new CurrentStateAuthorityCut(binding, null, mapping, null));

        var result = await reader.ReadAsync(binding.MachineId, CancellationToken.None);

        var noEvidence = Assert.IsType<CurrentMachineStateNoEvidence>(result);
        Assert.Equal(CurrentStateCoverage.Behind, noEvidence.Coverage);
    }

    [Fact]
    public async Task EvaluationBearingCutStopsAt3C4WithoutPolicyOrTimeObservation()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 5, mappedHighWater: 4);
        var evaluation = CreateEvaluation(binding, evaluatedThrough: 4);
        var reader = CreateReader(
            binding,
            new CurrentStateAuthorityCut(binding, null, mapping, evaluation));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            reader.ReadAsync(binding.MachineId, CancellationToken.None));

        Assert.Contains("FC-031.3C.4", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Complete", exception.Message, StringComparison.Ordinal);
    }

    private static CurrentMachineStateReader CreateReader(
        CurrentStateAuthorityBinding binding,
        CurrentStateAuthorityCut cut) =>
        new(
            new OwnerResolver(binding),
            new CutProvider(cut),
            new ThrowingPolicyResolver<CurrentStateContinuityPolicy>(),
            new ThrowingPolicyResolver<ICurrentStateFreshnessPolicy>(),
            new ThrowingTimeProvider());

    private static CurrentStateAuthorityBinding CreateBinding()
    {
        var machineId = MachineId.New();
        return new CurrentStateAuthorityBinding(
            machineId,
            new ObservationStreamId(machineId, "primary"),
            new ObservationProcessorId("mapper"),
            new ObservationProcessorId("state"));
    }

    private static MappingCoverageAuthority CreateMapping(
        CurrentStateAuthorityBinding binding,
        ulong rawThrough,
        ulong? mappedHighWater) =>
        new(
            binding.MappingProcessorId,
            binding.ObservationStreamId,
            new ObservationPosition(rawThrough),
            mappedHighWater is null ? null : new ObservationPosition(mappedHighWater.Value),
            new MappingAuthorityRevision(1));

    private static EvaluationAuthority CreateEvaluation(
        CurrentStateAuthorityBinding binding,
        ulong evaluatedThrough) =>
        new(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            new ObservationPosition(evaluatedThrough),
            MachineState.Running,
            1,
            new CurrentStatePolicyReference("continuity/preserve", "1.0"),
            new StateProjectionAuthorityRevision(1));

    private sealed class OwnerResolver(CurrentStateAuthorityBinding binding)
        : ICurrentStateOwnerResolver
    {
        public CurrentStateOwnerResolution Resolve(MachineId machineId) =>
            new CurrentStateExactlyOneOwner(binding);
    }

    private sealed class CutProvider(CurrentStateAuthorityCut cut)
        : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
            CurrentStateAuthorityBinding binding,
            CancellationToken cancellationToken) =>
            Task.FromResult<CurrentStateAuthorityCutReadResult>(new StableCurrentStateAuthorityCut(cut));
    }

    private sealed class ThrowingPolicyResolver<TPolicy> : ICurrentStatePolicyResolver<TPolicy>
        where TPolicy : class
    {
        public CurrentStatePolicyResolution<TPolicy> Resolve() =>
            throw new InvalidOperationException("Policy resolution is not authorized in FC-031.3C.3.");
    }

    private sealed class ThrowingTimeProvider : ICurrentStateTimeProvider
    {
        public DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("Temporal observation is not authorized in FC-031.3C.3.");
    }
}
