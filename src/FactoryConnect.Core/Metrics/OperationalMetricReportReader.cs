using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricReportReader : IOperationalMetricReportReader
{
    private readonly IOperationalMetricProjectionQueryReader _projectionReader;

    public OperationalMetricReportReader(IOperationalMetricProjectionQueryReader projectionReader)
    {
        ArgumentNullException.ThrowIfNull(projectionReader);
        _projectionReader = projectionReader;
    }

    public async ValueTask<ShiftOperationalMetricReport?> ReadShiftAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ShiftOccurrenceId shiftOccurrenceId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        var periodId = new OperationalMetricPeriodId.Shift(shiftOccurrenceId);
        var metrics = await ReadItemsAsync(
            processorId,
            machineId,
            periodId,
            contextKey,
            cancellationToken).ConfigureAwait(false);

        return metrics.Count == 0
            ? null
            : new ShiftOperationalMetricReport(
                processorId,
                machineId,
                shiftOccurrenceId,
                contextKey,
                metrics);
    }

    public async ValueTask<ProductionDayOperationalMetricReport?> ReadProductionDayAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ProductionDayId productionDayId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productionDayId);
        var periodId = new OperationalMetricPeriodId.ProductionDay(productionDayId);
        var metrics = await ReadItemsAsync(
            processorId,
            machineId,
            periodId,
            contextKey,
            cancellationToken).ConfigureAwait(false);

        return metrics.Count == 0
            ? null
            : new ProductionDayOperationalMetricReport(
                processorId,
                machineId,
                productionDayId,
                contextKey,
                metrics);
    }

    private async ValueTask<ReadOnlyCollection<OperationalMetricReportItem>> ReadItemsAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(periodId);
        ArgumentNullException.ThrowIfNull(contextKey);
        contextKey.Validate();

        var projections = await _projectionReader.ReadPeriodAsync(
            processorId,
            machineId,
            periodId,
            contextKey,
            cancellationToken).ConfigureAwait(false);

        if (projections.Any(projection =>
            projection.ProcessorId != processorId ||
            projection.Key.MachineId != machineId ||
            projection.Key.PeriodId != periodId ||
            projection.Key.ContextKey != contextKey))
        {
            throw new InvalidDataException(
                "Projection query reader returned data outside the requested reporting identity.");
        }

        var duplicate = projections
            .GroupBy(static projection => projection.Key.DefinitionId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Projection query reader returned duplicate metric definition '{duplicate.Key.MetricKey}/{duplicate.Key.Version}'.");
        }

        var metrics = projections
            .OrderBy(static projection => projection.Key.DefinitionId.MetricKey, StringComparer.Ordinal)
            .ThenBy(static projection => projection.Key.DefinitionId.Version, StringComparer.Ordinal)
            .Select(static projection => new OperationalMetricReportItem(projection))
            .ToArray();

        return new ReadOnlyCollection<OperationalMetricReportItem>(metrics);
    }
}
