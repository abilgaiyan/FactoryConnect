using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class CurrentMachineStateReader : ICurrentMachineStateReader
{
    private readonly ICurrentStateOwnerResolver _ownerResolver;
    private readonly ICurrentStateAuthorityCutProvider _authorityCutProvider;
    private readonly ICurrentStatePolicyResolver<CurrentStateContinuityPolicy> _continuityPolicyResolver;
    private readonly ICurrentStatePolicyResolver<ICurrentStateFreshnessPolicy> _freshnessPolicyResolver;
    private readonly ICurrentStateTimeProvider _timeProvider;

    public CurrentMachineStateReader(
        ICurrentStateOwnerResolver ownerResolver,
        ICurrentStateAuthorityCutProvider authorityCutProvider,
        ICurrentStatePolicyResolver<CurrentStateContinuityPolicy> continuityPolicyResolver,
        ICurrentStatePolicyResolver<ICurrentStateFreshnessPolicy> freshnessPolicyResolver,
        ICurrentStateTimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(ownerResolver);
        ArgumentNullException.ThrowIfNull(authorityCutProvider);
        ArgumentNullException.ThrowIfNull(continuityPolicyResolver);
        ArgumentNullException.ThrowIfNull(freshnessPolicyResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _ownerResolver = ownerResolver;
        _authorityCutProvider = authorityCutProvider;
        _continuityPolicyResolver = continuityPolicyResolver;
        _freshnessPolicyResolver = freshnessPolicyResolver;
        _timeProvider = timeProvider;
    }

    public async Task<CurrentMachineStateReadResult> ReadAsync(
        MachineId machineId,
        CancellationToken cancellationToken)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        var ownerResolution = _ownerResolver.Resolve(machineId);

        switch (ownerResolution)
        {
            case CurrentStateNoOwner:
                return new CurrentMachineStateAuthorityFailure(
                    machineId,
                    CurrentStateAuthorityFailureReason.NoOwner);

            case CurrentStateAmbiguousOwner:
                return new CurrentMachineStateAuthorityFailure(
                    machineId,
                    CurrentStateAuthorityFailureReason.AmbiguousOwner);

            case CurrentStateExactlyOneOwner exactlyOneOwner:
                return await ReadOwnedAsync(
                    machineId,
                    exactlyOneOwner.Binding,
                    cancellationToken).ConfigureAwait(false);

            default:
                throw new InvalidOperationException(
                    $"Unsupported current-state owner resolution '{ownerResolution?.GetType().FullName ?? "<null>"}'.");
        }
    }

    private async Task<CurrentMachineStateReadResult> ReadOwnedAsync(
        MachineId machineId,
        CurrentStateAuthorityBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding.MachineId != machineId)
        {
            throw new InvalidOperationException(
                "Current-state owner resolution returned a binding for a different machine.");
        }

        var cutResult = await _authorityCutProvider
            .ReadAuthorityCutAsync(binding, cancellationToken)
            .ConfigureAwait(false);

        switch (cutResult)
        {
            case StableCurrentStateAuthorityCutUnavailable:
                return new CurrentMachineStateAuthorityFailure(
                    machineId,
                    CurrentStateAuthorityFailureReason.StableCutUnavailable);

            case StableCurrentStateAuthorityCut stable:
                return ContinueFromStableCut(machineId, binding, stable.Cut);

            default:
                throw new InvalidOperationException(
                    $"Unsupported current-state authority-cut result '{cutResult?.GetType().FullName ?? "<null>"}'.");
        }
    }

    internal static CurrentMachineStateReadResult ContinueFromStableCut(
        MachineId machineId,
        CurrentStateAuthorityBinding binding,
        CurrentStateAuthorityCut cut)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(cut);

        if (cut.Binding != binding)
        {
            throw new InvalidOperationException(
                "Stable current-state authority cut does not retain the selected owner binding.");
        }

        var coverage = ClassifyCoverage(cut.MappingCoverage, cut.Evaluation);

        if (cut.Evaluation is null)
        {
            return new CurrentMachineStateNoEvidence(machineId, coverage);
        }

        // FC-031.3C.4 owns continuity-policy authority and E admissibility.
        throw new NotSupportedException(
            $"Evaluation-bearing current-state interpretation ({coverage}) is not implemented until FC-031.3C.4.");
    }

    internal static CurrentStateCoverage ClassifyCoverage(
        MappingCoverageAuthority? mappingCoverage,
        EvaluationAuthority? evaluation)
    {
        if (mappingCoverage is null)
        {
            return CurrentStateCoverage.Indeterminate;
        }

        var mappedHighWater = mappingCoverage.MappedEvaluationInputHighWater;
        if (mappedHighWater is null)
        {
            return CurrentStateCoverage.Complete;
        }

        if (evaluation is null)
        {
            return CurrentStateCoverage.Behind;
        }

        if (evaluation.EvaluatedThrough < mappedHighWater)
        {
            return CurrentStateCoverage.Behind;
        }

        if (evaluation.EvaluatedThrough == mappedHighWater)
        {
            return CurrentStateCoverage.Complete;
        }

        throw new InvalidOperationException(
            "Stable authority cut contains evaluation beyond the mapped evaluation-input frontier.");
    }
}
