namespace FactoryConnect.Persistence.SqlServer;

public sealed record SupervisorDeclaredIdealTimeResult(
    Guid EntryId, Guid? CycleRevisionId, decimal? DeclaredTotalQuantity,
    decimal? IdealProductionSeconds, string Status);

/// <summary>Original declaration preview only. Not reviewed quantity, reference time or OEE.</summary>
public static class SupervisorDeclaredIdealTime
{
    public static SupervisorDeclaredIdealTimeResult Calculate(
        SupervisorProductionEntryRecord entry,
        IEnumerable<SupervisorShiftIdealCycleRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(revisions);
        if (entry.GoodQuantity is not long good || entry.RejectedQuantity is not long rejected ||
            good < 0 || rejected < 0)
            return new(entry.EntryId, null, null, null, "quantity-unavailable");
        decimal total = (decimal)good + rejected;
        var cycle = SupervisorProductionCycleMatcher.Match(entry, revisions);
        if (cycle is null)
            return new(entry.EntryId, null, total, null, "cycle-unmatched");
        return new(entry.EntryId, cycle.CycleRevisionId, total,
            total * cycle.IdealCycleSeconds, "declared-only");
    }
}
