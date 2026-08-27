namespace FactoryConnect.Abstractions;

internal static class MetricInputOwnershipValidator
{
    public static void Validate(
        MetricInputStreamId streamId,
        DurableMetricInputFact fact,
        ShiftOccurrenceId shiftOccurrenceId,
        ProductionDayId productionDayId,
        string parameterName)
    {
        if (fact.Id.IsEmpty)
        {
            throw new ArgumentException(
                "Metric input fact identifier must not be empty.",
                parameterName);
        }

        if (fact.MachineId != streamId.MachineId)
        {
            throw new ArgumentException(
                "Metric input fact must belong to the metric input stream machine.",
                parameterName);
        }

        if (fact.SiteId != shiftOccurrenceId.SiteId ||
            fact.SiteId != productionDayId.SiteId)
        {
            throw new ArgumentException(
                "Metric input fact and temporal ownership must belong to the same site.",
                parameterName);
        }

        if (fact.ShiftId != shiftOccurrenceId.ShiftId)
        {
            throw new ArgumentException(
                "Metric input fact shift must match its shift occurrence ownership.",
                parameterName);
        }

        if (fact.ShiftScheduleAssignmentId !=
            shiftOccurrenceId.ShiftScheduleAssignmentId)
        {
            throw new ArgumentException(
                "Metric input fact shift schedule lineage must exactly match its shift occurrence ownership.",
                parameterName);
        }

        if (fact.StartsAtUtc < shiftOccurrenceId.StartsAtUtc ||
            fact.EndsAtUtc > shiftOccurrenceId.EndsAtUtc)
        {
            throw new ArgumentException(
                "Metric input fact interval must be contained by its shift occurrence ownership.",
                parameterName);
        }
    }
}
