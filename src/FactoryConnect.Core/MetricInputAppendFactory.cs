using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public static class MetricInputAppendFactory
{
    public static IReadOnlyList<DurableMetricInputAppend> Create(
        MetricInputStreamId streamId,
        IReadOnlyList<DurableMetricInputFact> facts,
        IReadOnlyList<ShiftOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(occurrences);

        var result = new List<DurableMetricInputAppend>(facts.Count);

        foreach (var fact in facts)
        {
            ArgumentNullException.ThrowIfNull(fact);

            var matches = occurrences
                .Where(occurrence =>
                    occurrence.SiteId == fact.SiteId &&
                    occurrence.ShiftId == fact.ShiftId &&
                    occurrence.SourceAssignmentId == fact.ShiftScheduleAssignmentId &&
                    fact.StartsAtUtc >= occurrence.StartsAtUtc &&
                    fact.EndsAtUtc <= occurrence.EndsAtUtc)
                .ToArray();

            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    "Metric input fact must resolve to exactly one persisted shift occurrence ownership.");
            }

            var occurrence = matches[0];
            var shiftOccurrenceId = new ShiftOccurrenceId(
                occurrence.SiteId,
                occurrence.SourceAssignmentId,
                occurrence.ShiftId,
                occurrence.StartsAtUtc,
                occurrence.EndsAtUtc);
            var productionDayId = new ProductionDayId(
                occurrence.SiteId,
                occurrence.FactoryDate);

            result.Add(new DurableMetricInputAppend(
                streamId,
                fact,
                shiftOccurrenceId,
                productionDayId));
        }

        return result;
    }
}
