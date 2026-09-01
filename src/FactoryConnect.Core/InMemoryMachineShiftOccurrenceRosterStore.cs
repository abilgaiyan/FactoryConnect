using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryMachineShiftOccurrenceRosterStore :
    IMachineShiftOccurrenceRosterStore
{
    private readonly object _sync = new();
    private readonly Dictionary<(MachineId MachineId, ProductionDayId ProductionDayId),
        MachineShiftOccurrenceRoster> _rosters = [];

    public ValueTask<MachineShiftOccurrenceRoster?> ReadAsync(
        MachineId machineId,
        ProductionDayId productionDayId,
        CancellationToken cancellationToken)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(productionDayId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _rosters.TryGetValue((machineId, productionDayId), out var roster);
            return ValueTask.FromResult(roster);
        }
    }

    public ValueTask CommitAsync(
        MachineShiftOccurrenceRosterCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var proposed = commit.ProposedRoster;
            var key = (proposed.MachineId, proposed.ProductionDayId);
            _rosters.TryGetValue(key, out var current);

            if (current?.Revision != commit.ExpectedRevision)
            {
                throw new InvalidOperationException(
                    "Machine-shift occurrence roster revision conflict.");
            }

            var staged = new Dictionary<(MachineId, ProductionDayId), MachineShiftOccurrenceRoster>(
                _rosters)
            {
                [key] = proposed,
            };

            var conflictingOwnership = staged.Values
                .SelectMany(static roster => roster.Occurrences)
                .GroupBy(static ownership => ownership.ShiftOccurrenceId)
                .FirstOrDefault(group =>
                    group.Select(static ownership => ownership.ProductionDayId)
                        .Distinct()
                        .Skip(1)
                        .Any());
            if (conflictingOwnership is not null)
            {
                throw new InvalidOperationException(
                    "A shift occurrence cannot belong to conflicting production days.");
            }

            _rosters[key] = proposed;
            return ValueTask.CompletedTask;
        }
    }
}
