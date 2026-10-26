using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

/// <summary>
/// Selects shift occurrences from previously materialized machine/production-day
/// roster authority and correlates zero-or-more FC-027 metric evaluations.
/// Reporting never resolves schedules and metric evidence never creates shifts.
/// </summary>
public sealed class ProductionDayShiftOperationalMetricReader :
    IProductionDayShiftOperationalMetricReader
{
    private readonly IMachineShiftOccurrenceRosterStore _rosterStore;
    private readonly IOperationalMetricReportReader _metricReader;

    public ProductionDayShiftOperationalMetricReader(
        IMachineShiftOccurrenceRosterStore rosterStore,
        IOperationalMetricReportReader metricReader)
    {
        ArgumentNullException.ThrowIfNull(rosterStore);
        ArgumentNullException.ThrowIfNull(metricReader);
        _rosterStore = rosterStore;
        _metricReader = metricReader;
    }

    public async ValueTask<IReadOnlyList<ProductionDayShiftOperationalMetricReport>> ReadAsync(
        ProductionDayShiftOperationalMetricQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // Resolve all authoritative roster coverage before reading any metric publication.
        var selections = new List<(ProductionDayShiftReportingSource Selection, MachineShiftOccurrenceRoster Roster)>();
        foreach (var selection in query.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roster = await _rosterStore.ReadAsync(selection.Source.MachineId,
                selection.ProductionDayId, cancellationToken).ConfigureAwait(false)
                ?? throw new ProductionDayShiftRosterCoverageRequiredException(
                    selection.Source.MachineId, selection.ProductionDayId);
            ValidateRoster(selection, roster);
            selections.Add((selection, roster));
        }

        var results = new List<ProductionDayShiftOperationalMetricReport>();
        foreach (var group in selections.GroupBy(static item => item.Selection.Source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = group.Key;
            var occurrences = group.SelectMany(item => item.Roster.Occurrences.Select(
                ownership => (Roster: item.Roster, Ownership: ownership))).ToArray();
            var metricReports = await _metricReader.ReadShiftsAsync(
                source.ProcessorId, source.MachineId,
                occurrences.Select(static item => item.Ownership.ShiftOccurrenceId).ToArray(),
                query.ContextKey, cancellationToken).ConfigureAwait(false);
            if (metricReports.Count != occurrences.Length)
                throw new InvalidDataException("Operational metric reader returned incomplete shift selection.");

            for (var index = 0; index < occurrences.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var roster = occurrences[index].Roster;
                var ownership = occurrences[index].Ownership;
                var metricReport = metricReports[index];

                if (metricReport is not null)
                {
                    ValidateMetricReport(source, ownership.ShiftOccurrenceId, query.ContextKey, metricReport);
                }

                var metrics = metricReport?.Metrics
                    .Where(metric => query.Metrics is null ||
                        query.Metrics.DefinitionIds.Contains(metric.DefinitionId))
                    .Where(metric => query.Statuses is null ||
                        query.Statuses.Statuses.Contains(metric.Status))
                    .OrderBy(static metric => metric.DefinitionId.MetricKey, StringComparer.Ordinal)
                    .ThenBy(static metric => metric.DefinitionId.Version, StringComparer.Ordinal)
                    .ToArray()
                    ?? [];

                results.Add(new ProductionDayShiftOperationalMetricReport(
                    source,
                    roster.ProductionDayId,
                    roster.ProductionLineId,
                    ownership.ShiftOccurrenceId,
                    query.ContextKey,
                    metricReport?.SourceRevision,
                    metrics));
            }
        }

        return results
            .OrderBy(static report => report.Source.MachineId.Value)
            .ThenBy(static report => report.Source.ProcessorId.Value, StringComparer.Ordinal)
            .ThenBy(static report => report.ProductionDayId.BusinessDate)
            .ThenBy(static report => report.ProductionDayId.SiteId.Value, StringComparer.Ordinal)
            .ThenBy(static report => report.ShiftOccurrenceId.StartsAtUtc)
            .ThenBy(static report => report.ShiftOccurrenceId.EndsAtUtc)
            .ThenBy(static report => report.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value, StringComparer.Ordinal)
            .ThenBy(static report => report.ShiftOccurrenceId.ShiftId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateRoster(
        ProductionDayShiftReportingSource selection,
        MachineShiftOccurrenceRoster roster)
    {
        if (roster.MachineId != selection.Source.MachineId ||
            roster.ProductionDayId != selection.ProductionDayId)
        {
            throw new InvalidDataException(
                "Machine-shift roster store returned coverage outside the requested machine/production-day identity.");
        }
    }

    private static void ValidateMetricReport(
        OperationalMetricReportingSource source,
        ShiftOccurrenceId shiftOccurrenceId,
        OperationalMetricEvaluationContextKey contextKey,
        ShiftOperationalMetricReport report)
    {
        if (report.ProcessorId != source.ProcessorId ||
            report.MachineId != source.MachineId ||
            report.ShiftOccurrenceId != shiftOccurrenceId ||
            report.ContextKey != contextKey)
        {
            throw new InvalidDataException(
                "Operational metric reader returned a shift report outside the authoritative roster selection.");
        }
    }
}
