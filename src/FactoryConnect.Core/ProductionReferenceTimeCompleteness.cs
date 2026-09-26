using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

/// <summary>Requires an explicit resolution for every produced-quantity source in a period.</summary>
public static class ProductionReferenceTimeCompleteness
{
    public static bool IsComplete(
        IReadOnlyList<ProductionQuantityEvidenceId> producedSources,
        IReadOnlyList<ProductionReferenceTimeResolution> outcomes,
        MachineId machineId,
        ShiftOccurrenceId? shiftOccurrenceId,
        ProductionDayId? productionDayId)
    {
        ArgumentNullException.ThrowIfNull(producedSources);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (machineId.IsEmpty || (shiftOccurrenceId is null) == (productionDayId is null))
        {
            throw new ArgumentException("Exactly one period and a machine are required.");
        }

        var sources = new HashSet<ProductionQuantityEvidenceId>();
        foreach (var source in producedSources)
        {
            if (source.IsEmpty || !sources.Add(source))
            {
                throw new ArgumentException("Produced sources must have distinct nonempty identities.");
            }
        }

        // No produced quantity does not establish a reference-time duration.
        if (sources.Count == 0)
        {
            return false;
        }

        var resolved = new HashSet<ProductionQuantityEvidenceId>();
        var allResolved = true;
        foreach (var outcome in outcomes)
        {
            ArgumentNullException.ThrowIfNull(outcome);
            outcome.Validate();
            if (outcome.MachineId != machineId ||
                (shiftOccurrenceId is not null && outcome.ShiftOccurrenceId != shiftOccurrenceId) ||
                (productionDayId is not null && outcome.ProductionDayId != productionDayId))
            {
                throw new InvalidOperationException("Reference-time outcomes must belong to the evaluated machine and period.");
            }

            if (!sources.Contains(outcome.SourceQuantityEvidenceId) ||
                !resolved.Add(outcome.SourceQuantityEvidenceId))
            {
                throw new InvalidOperationException("Reference-time outcomes must correspond one-to-one with produced sources.");
            }

            if (outcome.Status != ProductionReferenceTimeResolutionStatus.Resolved)
            {
                allResolved = false;
            }
        }

        return allResolved && resolved.SetEquals(sources);
    }
}
