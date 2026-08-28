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
        var result = await ReadItemsAsync(
            processorId,
            machineId,
            periodId,
            contextKey,
            cancellationToken).ConfigureAwait(false);

        return result is null
            ? null
            : new ShiftOperationalMetricReport(
                processorId,
                machineId,
                shiftOccurrenceId,
                contextKey,
                result.SourceRevision,
                result.Metrics);
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
        var result = await ReadItemsAsync(
            processorId,
            machineId,
            periodId,
            contextKey,
            cancellationToken).ConfigureAwait(false);

        return result is null
            ? null
            : new ProductionDayOperationalMetricReport(
                processorId,
                machineId,
                productionDayId,
                contextKey,
                result.SourceRevision,
                result.Metrics);
    }

    public async ValueTask<OperationalMetricReportDetail?> ReadMetricDetailAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        OperationalMetricDefinitionId definitionId,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(processorId, machineId, periodId, contextKey);
        ArgumentNullException.ThrowIfNull(definitionId);

        var key = new OperationalMetricEvaluationKey(
            machineId,
            periodId,
            definitionId,
            contextKey);
        var projection = await _projectionReader.ReadDetailAsync(
            processorId,
            key,
            cancellationToken).ConfigureAwait(false);

        if (projection is null)
        {
            return null;
        }

        if (projection.ProcessorId != processorId || projection.Key != key)
        {
            throw new InvalidDataException(
                "Projection detail reader returned data outside the requested exact metric identity.");
        }

        return new OperationalMetricReportDetail(projection);
    }

    private async ValueTask<PeriodReadResult?> ReadItemsAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(processorId, machineId, periodId, contextKey);

        var summaries = await _projectionReader.ReadPeriodSummariesAsync(
            processorId,
            machineId,
            periodId,
            contextKey,
            cancellationToken).ConfigureAwait(false);

        if (summaries.Count == 0)
        {
            return null;
        }

        if (summaries.Any(summary =>
            summary.ProcessorId != processorId ||
            summary.Key.MachineId != machineId ||
            summary.Key.PeriodId != periodId ||
            summary.Key.ContextKey != contextKey))
        {
            throw new InvalidDataException(
                "Projection summary reader returned data outside the requested reporting identity.");
        }

        var duplicate = summaries
            .GroupBy(static summary => summary.Key.DefinitionId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Projection summary reader returned duplicate metric definition '{duplicate.Key.MetricKey}/{duplicate.Key.Version}'.");
        }

        var sourceRevision = summaries[0].SourceRevision;
        if (summaries.Any(summary => summary.SourceRevision != sourceRevision))
        {
            throw new InvalidDataException(
                "Projection summary reader returned mixed FC-026 source revisions for one period report.");
        }

        var metrics = summaries
            .OrderBy(static summary => summary.Key.DefinitionId.MetricKey, StringComparer.Ordinal)
            .ThenBy(static summary => summary.Key.DefinitionId.Version, StringComparer.Ordinal)
            .Select(static summary => new OperationalMetricReportItem(summary))
            .ToArray();

        return new PeriodReadResult(
            sourceRevision,
            new ReadOnlyCollection<OperationalMetricReportItem>(metrics));
    }

    private static void ValidateIdentity(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(periodId);
        ArgumentNullException.ThrowIfNull(contextKey);
        contextKey.Validate();
    }

    private sealed record PeriodReadResult(
        MetricAggregationCheckpoint SourceRevision,
        ReadOnlyCollection<OperationalMetricReportItem> Metrics);
}
