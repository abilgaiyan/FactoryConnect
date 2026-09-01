using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class MachineShiftRosterCoverageRequiredException : InvalidOperationException
{
    public MachineShiftRosterCoverageRequiredException(
        MachineId machineId,
        ProductionDayId productionDayId)
        : base($"Authoritative machine-shift roster coverage is required for machine '{machineId}' and production day '{productionDayId}'.")
    {
        MachineId = machineId;
        ProductionDayId = productionDayId;
    }

    public MachineId MachineId { get; }

    public ProductionDayId ProductionDayId { get; }
}

public sealed class MachineShiftOccurrenceOwnershipMismatchException : InvalidOperationException
{
    public MachineShiftOccurrenceOwnershipMismatchException(
        MachineId machineId,
        ProductionLineId productionLineId,
        ProductionDayId productionDayId,
        ShiftOccurrenceId shiftOccurrenceId)
        : base($"Metric-input shift ownership does not match the authoritative machine-shift roster for machine '{machineId}' and production day '{productionDayId}'.")
    {
        MachineId = machineId;
        ProductionLineId = productionLineId;
        ProductionDayId = productionDayId;
        ShiftOccurrenceId = shiftOccurrenceId;
    }

    public MachineId MachineId { get; }

    public ProductionLineId ProductionLineId { get; }

    public ProductionDayId ProductionDayId { get; }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }
}

/// <summary>
/// Guards the FC-025 publication boundary with previously materialized
/// machine-shift roster authority. This decorator is read-only with respect
/// to roster state: it never creates, updates, infers, or repairs coverage.
/// </summary>
public sealed class RosterValidatedProductionContextProcessingStore :
    IProductionContextProcessingStore
{
    private readonly IProductionContextProcessingStore _inner;
    private readonly IMachineShiftOccurrenceRosterStore _rosterStore;
    private readonly IReadOnlyDictionary<MachineId, ProductionLineId> _machineLines;

    public RosterValidatedProductionContextProcessingStore(
        IProductionContextProcessingStore inner,
        IMachineShiftOccurrenceRosterStore rosterStore,
        IEnumerable<MachineShiftScheduleScope> schedulingScopes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(rosterStore);
        ArgumentNullException.ThrowIfNull(schedulingScopes);

        var scopes = schedulingScopes.ToArray();
        if (scopes.Any(static scope => scope is null))
        {
            throw new ArgumentException(
                "Machine scheduling scopes cannot contain null values.",
                nameof(schedulingScopes));
        }

        var duplicateMachine = scopes
            .GroupBy(static scope => scope.MachineId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateMachine is not null)
        {
            throw new ArgumentException(
                "Machine scheduling scopes must contain unique machine identities.",
                nameof(schedulingScopes));
        }

        _inner = inner;
        _rosterStore = rosterStore;
        _machineLines = scopes.ToDictionary(
            static scope => scope.MachineId,
            static scope => scope.ProductionLineId);
    }

    public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken) =>
        _inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);

    public async Task CommitAsync(
        ProductionContextProcessingCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        var coverage = new Dictionary<
            (MachineId MachineId, ProductionDayId ProductionDayId),
            MachineShiftOccurrenceRoster>();

        foreach (var append in commit.MetricInputs)
        {
            ArgumentNullException.ThrowIfNull(append);
            var machineId = append.StreamId.MachineId;
            if (!_machineLines.TryGetValue(machineId, out var productionLineId))
            {
                throw new MachineShiftOccurrenceOwnershipMismatchException(
                    machineId,
                    append.Fact.ProductionLineId ?? default,
                    append.ProductionDayId,
                    append.ShiftOccurrenceId);
            }

            var key = (machineId, append.ProductionDayId);
            if (!coverage.TryGetValue(key, out var roster))
            {
                roster = await _rosterStore.ReadAsync(
                    machineId,
                    append.ProductionDayId,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new MachineShiftRosterCoverageRequiredException(
                        machineId,
                        append.ProductionDayId);
                coverage.Add(key, roster);
            }

            var factLineMatches = append.Fact.ProductionLineId is null ||
                append.Fact.ProductionLineId == productionLineId;
            var exactOwnershipExists = roster.MachineId == machineId &&
                roster.ProductionLineId == productionLineId &&
                roster.ProductionDayId == append.ProductionDayId &&
                roster.Occurrences.Any(ownership =>
                    ownership.MachineId == machineId &&
                    ownership.ProductionLineId == productionLineId &&
                    ownership.ProductionDayId == append.ProductionDayId &&
                    ownership.ShiftOccurrenceId == append.ShiftOccurrenceId);

            if (!factLineMatches || !exactOwnershipExists)
            {
                throw new MachineShiftOccurrenceOwnershipMismatchException(
                    machineId,
                    productionLineId,
                    append.ProductionDayId,
                    append.ShiftOccurrenceId);
            }
        }

        await _inner.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
    }
}
