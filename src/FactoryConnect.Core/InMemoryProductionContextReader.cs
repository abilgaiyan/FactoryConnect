using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryProductionContextReader : IProductionContextReader
{
    private readonly List<ProductionContextAssignment> _assignments = [];

    public InMemoryProductionContextReader(
        IEnumerable<ProductionContextAssignment>? assignments = null)
    {
        if (assignments is null)
        {
            return;
        }

        foreach (var assignment in assignments)
        {
            Add(assignment);
        }
    }

    public void Add(ProductionContextAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Validate();

        foreach (var existing in _assignments)
        {
            if (existing.MachineId != assignment.MachineId)
            {
                continue;
            }

            if (IntervalsOverlap(existing, assignment))
            {
                throw new InvalidOperationException(
                    $"Production context assignment '{assignment.Id}' overlaps assignment '{existing.Id}' for machine '{assignment.MachineId}'.");
            }
        }

        _assignments.Add(assignment);
        _assignments.Sort(static (left, right) =>
        {
            var byStart = left.EffectiveFrom.CompareTo(right.EffectiveFrom);
            return byStart != 0
                ? byStart
                : string.CompareOrdinal(left.Id.Value, right.Id.Value);
        });
    }

    public Task<IReadOnlyList<ProductionContextAssignment>> ReadAsync(
        MachineId machineId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(to, from);

        IReadOnlyList<ProductionContextAssignment> result = _assignments
            .Where(assignment =>
                assignment.MachineId == machineId &&
                assignment.Intersects(from, to))
            .OrderBy(static assignment => assignment.EffectiveFrom)
            .ThenBy(static assignment => assignment.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }

    private static bool IntervalsOverlap(
        ProductionContextAssignment left,
        ProductionContextAssignment right)
    {
        var leftEndsAfterRightStarts =
            left.EffectiveTo is null || left.EffectiveTo.Value > right.EffectiveFrom;
        var rightEndsAfterLeftStarts =
            right.EffectiveTo is null || right.EffectiveTo.Value > left.EffectiveFrom;

        return leftEndsAfterRightStarts && rightEndsAfterLeftStarts;
    }
}
