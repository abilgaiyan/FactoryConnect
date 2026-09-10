namespace FactoryConnect.Abstractions;

public sealed record CurrentStateAuthorityBinding
{
    public CurrentStateAuthorityBinding(
        MachineId machineId,
        ObservationStreamId observationStreamId,
        ObservationProcessorId mappingProcessorId,
        ObservationProcessorId stateProcessorId)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(observationStreamId);
        ArgumentNullException.ThrowIfNull(mappingProcessorId);
        ArgumentNullException.ThrowIfNull(stateProcessorId);

        if (observationStreamId.MachineId != machineId)
        {
            throw new ArgumentException(
                "Observation stream must belong to the bound machine.",
                nameof(observationStreamId));
        }

        MachineId = machineId;
        ObservationStreamId = observationStreamId;
        MappingProcessorId = mappingProcessorId;
        StateProcessorId = stateProcessorId;
    }

    public MachineId MachineId { get; }

    public ObservationStreamId ObservationStreamId { get; }

    public ObservationProcessorId MappingProcessorId { get; }

    public ObservationProcessorId StateProcessorId { get; }
}

public sealed record AcquisitionAuthorityRevision(ulong Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record MappingAuthorityRevision(ulong Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record StateProjectionAuthorityRevision(ulong Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record CurrentStatePolicyReference
{
    public CurrentStatePolicyReference(string identity, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        Identity = identity;
        Version = version;
    }

    public string Identity { get; }

    public string Version { get; }
}

public enum StateContinuityMode
{
    Preserve = 0,
    Reset = 1,
}

public sealed record CurrentStateContinuityPolicy
{
    public CurrentStateContinuityPolicy(
        CurrentStatePolicyReference reference,
        StateContinuityMode mode)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Reference = reference;
        Mode = mode;
    }

    public CurrentStatePolicyReference Reference { get; }

    public StateContinuityMode Mode { get; }
}

public interface ICurrentStateFreshnessPolicy
{
    CurrentStatePolicyReference Reference { get; }
}

public sealed record AcquisitionContactAuthority
{
    public AcquisitionContactAuthority(
        ObservationStreamId observationStreamId,
        DateTimeOffset successfulContactTime,
        ObservationPosition? rawAcceptedThrough,
        AcquisitionAuthorityRevision acquisitionRevision)
    {
        ArgumentNullException.ThrowIfNull(observationStreamId);
        ArgumentNullException.ThrowIfNull(acquisitionRevision);

        ObservationStreamId = observationStreamId;
        SuccessfulContactTime = successfulContactTime;
        RawAcceptedThrough = rawAcceptedThrough;
        AcquisitionRevision = acquisitionRevision;
    }

    public ObservationStreamId ObservationStreamId { get; }

    public DateTimeOffset SuccessfulContactTime { get; }

    public ObservationPosition? RawAcceptedThrough { get; }

    public AcquisitionAuthorityRevision AcquisitionRevision { get; }
}

public sealed record MappingCoverageAuthority
{
    public MappingCoverageAuthority(
        ObservationProcessorId mappingProcessorId,
        ObservationStreamId observationStreamId,
        ObservationPosition rawConsumedThrough,
        ObservationPosition? mappedEvaluationInputHighWater,
        MappingAuthorityRevision mappingRevision)
    {
        ArgumentNullException.ThrowIfNull(mappingProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        ArgumentNullException.ThrowIfNull(rawConsumedThrough);
        ArgumentNullException.ThrowIfNull(mappingRevision);

        MappingProcessorId = mappingProcessorId;
        ObservationStreamId = observationStreamId;
        RawConsumedThrough = rawConsumedThrough;
        MappedEvaluationInputHighWater = mappedEvaluationInputHighWater;
        MappingRevision = mappingRevision;
    }

    public ObservationProcessorId MappingProcessorId { get; }

    public ObservationStreamId ObservationStreamId { get; }

    public ObservationPosition RawConsumedThrough { get; }

    public ObservationPosition? MappedEvaluationInputHighWater { get; }

    public MappingAuthorityRevision MappingRevision { get; }
}

public sealed record EvaluationAuthority
{
    public EvaluationAuthority(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        ObservationPosition evaluatedThrough,
        MachineState machineState,
        ulong lastConsumedInstanceId,
        CurrentStatePolicyReference appliedContinuityPolicy,
        StateProjectionAuthorityRevision projectionRevision)
    {
        ArgumentNullException.ThrowIfNull(stateProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        ArgumentNullException.ThrowIfNull(evaluatedThrough);
        ArgumentNullException.ThrowIfNull(appliedContinuityPolicy);
        ArgumentNullException.ThrowIfNull(projectionRevision);
        if (!Enum.IsDefined(machineState))
        {
            throw new ArgumentOutOfRangeException(nameof(machineState));
        }

        StateProcessorId = stateProcessorId;
        ObservationStreamId = observationStreamId;
        EvaluatedThrough = evaluatedThrough;
        MachineState = machineState;
        LastConsumedInstanceId = lastConsumedInstanceId;
        AppliedContinuityPolicy = appliedContinuityPolicy;
        ProjectionRevision = projectionRevision;
    }

    public ObservationProcessorId StateProcessorId { get; }

    public ObservationStreamId ObservationStreamId { get; }

    public ObservationPosition EvaluatedThrough { get; }

    public MachineState MachineState { get; }

    public ulong LastConsumedInstanceId { get; }

    public CurrentStatePolicyReference AppliedContinuityPolicy { get; }

    public StateProjectionAuthorityRevision ProjectionRevision { get; }
}

public sealed record CurrentStateAuthorityCut
{
    public CurrentStateAuthorityCut(
        CurrentStateAuthorityBinding binding,
        AcquisitionContactAuthority? acquisitionContact,
        MappingCoverageAuthority? mappingCoverage,
        EvaluationAuthority? evaluation)
    {
        ArgumentNullException.ThrowIfNull(binding);

        Binding = binding;
        AcquisitionContact = acquisitionContact;
        MappingCoverage = mappingCoverage;
        Evaluation = evaluation;
    }

    public CurrentStateAuthorityBinding Binding { get; }

    public AcquisitionContactAuthority? AcquisitionContact { get; }

    public MappingCoverageAuthority? MappingCoverage { get; }

    public EvaluationAuthority? Evaluation { get; }
}

public abstract record CurrentStateAuthorityCutReadResult;

public sealed record StableCurrentStateAuthorityCut : CurrentStateAuthorityCutReadResult
{
    public StableCurrentStateAuthorityCut(CurrentStateAuthorityCut cut)
    {
        ArgumentNullException.ThrowIfNull(cut);
        Cut = cut;
    }

    public CurrentStateAuthorityCut Cut { get; }
}

public sealed record StableCurrentStateAuthorityCutUnavailable : CurrentStateAuthorityCutReadResult
{
    public static StableCurrentStateAuthorityCutUnavailable Instance { get; } = new();

    private StableCurrentStateAuthorityCutUnavailable()
    {
    }
}

public interface ICurrentStateAuthorityCutProvider
{
    Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
        CurrentStateAuthorityBinding binding,
        CancellationToken cancellationToken);
}

public abstract record CurrentStateOwnerResolution;

public sealed record CurrentStateNoOwner : CurrentStateOwnerResolution
{
    public static CurrentStateNoOwner Instance { get; } = new();

    private CurrentStateNoOwner()
    {
    }
}

public sealed record CurrentStateExactlyOneOwner : CurrentStateOwnerResolution
{
    public CurrentStateExactlyOneOwner(CurrentStateAuthorityBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Binding = binding;
    }

    public CurrentStateAuthorityBinding Binding { get; }
}

public sealed record CurrentStateAmbiguousOwner : CurrentStateOwnerResolution
{
    public static CurrentStateAmbiguousOwner Instance { get; } = new();

    private CurrentStateAmbiguousOwner()
    {
    }
}

public abstract record CurrentStatePolicyResolution<TPolicy>
    where TPolicy : class;

public sealed record CurrentStateMissingPolicy<TPolicy> : CurrentStatePolicyResolution<TPolicy>
    where TPolicy : class;

public sealed record CurrentStateExactlyOnePolicy<TPolicy> : CurrentStatePolicyResolution<TPolicy>
    where TPolicy : class
{
    public CurrentStateExactlyOnePolicy(TPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
    }

    public TPolicy Policy { get; }
}

public sealed record CurrentStateAmbiguousPolicy<TPolicy> : CurrentStatePolicyResolution<TPolicy>
    where TPolicy : class;

public sealed record CurrentStateUnsupportedPolicy<TPolicy> : CurrentStatePolicyResolution<TPolicy>
    where TPolicy : class;

public enum CurrentStateCoverage
{
    Complete = 0,
    Behind = 1,
    Indeterminate = 2,
}

public enum CurrentStateFreshness
{
    Current = 0,
    Stale = 1,
    Indeterminate = 2,
}

public enum CurrentStateUsability
{
    Current = 0,
    Stale = 1,
    Indeterminate = 2,
    Behind = 3,
}

public enum CurrentStateAuthorityFailureReason
{
    NoOwner = 0,
    AmbiguousOwner = 1,
    MissingPolicyAuthority = 2,
    AmbiguousPolicyAuthority = 3,
    UnsupportedPolicyAuthority = 4,
    StableCutUnavailable = 5,
    ContradictoryAttribution = 6,
    ContinuityPolicyMismatch = 7,
    UnauthorizedStateAuthority = 8,
    InconsistentAuthority = 9,
}

public abstract record CurrentMachineStateReadResult
{
    private protected CurrentMachineStateReadResult(MachineId machineId)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        MachineId = machineId;
    }

    public MachineId MachineId { get; }
}

public sealed record CurrentMachineStateNoEvidence : CurrentMachineStateReadResult
{
    public CurrentMachineStateNoEvidence(MachineId machineId, CurrentStateCoverage coverage)
        : base(machineId)
    {
        if (!Enum.IsDefined(coverage))
        {
            throw new ArgumentOutOfRangeException(nameof(coverage));
        }

        Coverage = coverage;
    }

    public CurrentStateCoverage Coverage { get; }
}

public sealed record CurrentMachineStateEvidence : CurrentMachineStateReadResult
{
    public CurrentMachineStateEvidence(
        MachineId machineId,
        MachineState machineState,
        CurrentStateCoverage coverage,
        CurrentStateFreshness freshness,
        CurrentStateUsability usability,
        DateTimeOffset readAsOf)
        : base(machineId)
    {
        if (!Enum.IsDefined(machineState))
        {
            throw new ArgumentOutOfRangeException(nameof(machineState));
        }

        if (!Enum.IsDefined(coverage))
        {
            throw new ArgumentOutOfRangeException(nameof(coverage));
        }

        if (!Enum.IsDefined(freshness))
        {
            throw new ArgumentOutOfRangeException(nameof(freshness));
        }

        if (!Enum.IsDefined(usability))
        {
            throw new ArgumentOutOfRangeException(nameof(usability));
        }

        if (!IsCompatible(coverage, freshness, usability))
        {
            throw new ArgumentException(
                "Usability must match the frozen coverage/freshness precedence.",
                nameof(usability));
        }

        MachineState = machineState;
        Coverage = coverage;
        Freshness = freshness;
        Usability = usability;
        ReadAsOf = readAsOf;
    }

    public MachineState MachineState { get; }

    public CurrentStateCoverage Coverage { get; }

    public CurrentStateFreshness Freshness { get; }

    public CurrentStateUsability Usability { get; }

    public DateTimeOffset ReadAsOf { get; }

    private static bool IsCompatible(
        CurrentStateCoverage coverage,
        CurrentStateFreshness freshness,
        CurrentStateUsability usability) =>
        coverage switch
        {
            CurrentStateCoverage.Behind => usability == CurrentStateUsability.Behind,
            CurrentStateCoverage.Indeterminate => usability == CurrentStateUsability.Indeterminate,
            CurrentStateCoverage.Complete when freshness == CurrentStateFreshness.Indeterminate =>
                usability == CurrentStateUsability.Indeterminate,
            CurrentStateCoverage.Complete when freshness == CurrentStateFreshness.Stale =>
                usability == CurrentStateUsability.Stale,
            CurrentStateCoverage.Complete when freshness == CurrentStateFreshness.Current =>
                usability == CurrentStateUsability.Current,
            _ => false,
        };
}

public sealed record CurrentMachineStateAuthorityFailure : CurrentMachineStateReadResult
{
    public CurrentMachineStateAuthorityFailure(
        MachineId machineId,
        CurrentStateAuthorityFailureReason reason)
        : base(machineId)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Reason = reason;
    }

    public CurrentStateAuthorityFailureReason Reason { get; }
}

public interface ICurrentMachineStateReader
{
    Task<CurrentMachineStateReadResult> ReadAsync(
        MachineId machineId,
        CancellationToken cancellationToken);
}
