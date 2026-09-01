using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public sealed record MachineShiftOccurrenceRosterRevision
{
    public MachineShiftOccurrenceRosterRevision(ulong value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Machine-shift occurrence roster revision must be greater than zero.");
        }

        Value = value;
    }

    public ulong Value { get; }

    public override string ToString() => Value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record MachineShiftOccurrenceOwnership
{
    public MachineShiftOccurrenceOwnership(
        MachineId machineId,
        ProductionLineId productionLineId,
        ShiftOccurrenceId shiftOccurrenceId,
        ProductionDayId productionDayId)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        if (productionLineId.IsEmpty)
        {
            throw new ArgumentException(
                "Production line ID is required.",
                nameof(productionLineId));
        }

        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        ArgumentNullException.ThrowIfNull(productionDayId);

        if (shiftOccurrenceId.SiteId != productionDayId.SiteId)
        {
            throw new ArgumentException(
                "Shift occurrence and production day must belong to the same site.",
                nameof(productionDayId));
        }

        MachineId = machineId;
        ProductionLineId = productionLineId;
        ShiftOccurrenceId = shiftOccurrenceId;
        ProductionDayId = productionDayId;
    }

    public MachineId MachineId { get; }

    public ProductionLineId ProductionLineId { get; }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }

    public ProductionDayId ProductionDayId { get; }
}

public sealed record MachineShiftOccurrenceRoster
{
    public MachineShiftOccurrenceRoster(
        MachineId machineId,
        ProductionLineId productionLineId,
        ProductionDayId productionDayId,
        MachineShiftOccurrenceRosterRevision revision,
        IEnumerable<MachineShiftOccurrenceOwnership> occurrences)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        if (productionLineId.IsEmpty)
        {
            throw new ArgumentException(
                "Production line ID is required.",
                nameof(productionLineId));
        }

        ArgumentNullException.ThrowIfNull(productionDayId);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(occurrences);

        var snapshot = occurrences.ToArray();
        if (snapshot.Any(static occurrence => occurrence is null))
        {
            throw new ArgumentException(
                "Machine-shift occurrence rosters cannot contain null ownership entries.",
                nameof(occurrences));
        }

        if (snapshot.Any(occurrence =>
                occurrence.MachineId != machineId ||
                occurrence.ProductionLineId != productionLineId ||
                occurrence.ProductionDayId != productionDayId))
        {
            throw new ArgumentException(
                "Every occurrence must belong to the roster machine, line, and production day.",
                nameof(occurrences));
        }

        if (snapshot
            .GroupBy(static occurrence => occurrence.ShiftOccurrenceId)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Machine-shift occurrence rosters cannot contain duplicate shift occurrence identities.",
                nameof(occurrences));
        }

        MachineId = machineId;
        ProductionLineId = productionLineId;
        ProductionDayId = productionDayId;
        Revision = revision;
        Occurrences = new ReadOnlyCollection<MachineShiftOccurrenceOwnership>(
            snapshot
                .OrderBy(static occurrence => occurrence.ShiftOccurrenceId.StartsAtUtc)
                .ThenBy(static occurrence => occurrence.ShiftOccurrenceId.EndsAtUtc)
                .ThenBy(
                    static occurrence => occurrence.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value,
                    StringComparer.Ordinal)
                .ThenBy(
                    static occurrence => occurrence.ShiftOccurrenceId.ShiftId.Value,
                    StringComparer.Ordinal)
                .ToArray());
    }

    public MachineId MachineId { get; }

    public ProductionLineId ProductionLineId { get; }

    public ProductionDayId ProductionDayId { get; }

    public MachineShiftOccurrenceRosterRevision Revision { get; }

    public IReadOnlyList<MachineShiftOccurrenceOwnership> Occurrences { get; }
}

public sealed record MachineShiftOccurrenceRosterCommit
{
    public MachineShiftOccurrenceRosterCommit(
        MachineShiftOccurrenceRosterRevision? expectedRevision,
        MachineShiftOccurrenceRoster proposedRoster)
    {
        ArgumentNullException.ThrowIfNull(proposedRoster);

        ExpectedRevision = expectedRevision;
        ProposedRoster = proposedRoster;
    }

    public MachineShiftOccurrenceRosterRevision? ExpectedRevision { get; }

    public MachineShiftOccurrenceRoster ProposedRoster { get; }
}

public interface IMachineShiftOccurrenceRosterStore
{
    ValueTask<MachineShiftOccurrenceRoster?> ReadAsync(
        MachineId machineId,
        ProductionDayId productionDayId,
        CancellationToken cancellationToken);

    ValueTask CommitAsync(
        MachineShiftOccurrenceRosterCommit commit,
        CancellationToken cancellationToken);
}
