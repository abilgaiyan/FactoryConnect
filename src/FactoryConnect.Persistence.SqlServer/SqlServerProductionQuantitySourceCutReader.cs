using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed partial class SqlServerMetricAggregationStore : IProductionQuantitySourceCutReader
{
    public async ValueTask<ProductionQuantitySourceCut> ReadProductionQuantitySourceCutAsync(
        MetricAggregationCheckpoint aggregationCheckpoint,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregationCheckpoint);
        ArgumentNullException.ThrowIfNull(periodId);

        if (aggregationCheckpoint.StreamId.MachineId != machineId)
        {
            throw new ArgumentException(
                "Source-cut machine must match the aggregation checkpoint stream machine.",
                nameof(machineId));
        }

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var processor = await FindProcessorAsync(
            connection,
            transaction: null,
            aggregationCheckpoint.ProcessorId,
            cancellationToken);
        if (processor is null)
        {
            throw HistoricalRevisionUnavailable();
        }

        await ValidateProcessorStreamAsync(
            connection,
            processor.Value.StreamRowId,
            aggregationCheckpoint.StreamId,
            cancellationToken);

        if (!await RevisionExistsAsync(
                connection,
                transaction: null,
                processor.Value.RowId,
                aggregationCheckpoint.Position,
                cancellationToken))
        {
            throw HistoricalRevisionUnavailable();
        }

        var contributions = await ReadHistoricalContributionsAsync(
            connection,
            processor.Value.RowId,
            processor.Value.StreamRowId,
            aggregationCheckpoint.Position,
            cancellationToken);

        var relevant = contributions
            .Where(item => item.Fact.MachineId == machineId)
            .Where(item => BelongsToPeriod(item, periodId))
            .Where(item => item.Fact.SourceQuantityEvidenceId is not null)
            .Where(item => item.Fact.Key is MetricInputFactKeys.PartCountIncrement
                or MetricInputFactKeys.GoodQuantity
                or MetricInputFactKeys.RejectedQuantity)
            .ToArray();

        var sources = relevant
            .GroupBy(item => item.Fact.SourceQuantityEvidenceId!.Value)
            .Select(group => ReconstructSource(group.Key, group.OrderBy(item => item.Position).ToArray()))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .OrderBy(entry => entry.ContributingPositions[0])
            .ThenBy(entry => entry.Evidence.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return new ProductionQuantitySourceCut(
            aggregationCheckpoint,
            periodId,
            sources);
    }

    private static bool BelongsToPeriod(
        PositionedMetricInputFact item,
        OperationalMetricPeriodId periodId) =>
        periodId switch
        {
            OperationalMetricPeriodId.Shift shift =>
                item.ShiftOccurrenceId == shift.ShiftOccurrenceId,
            OperationalMetricPeriodId.ProductionDay day =>
                item.ProductionDayId == day.ProductionDayId,
            _ => throw new InvalidOperationException("Unsupported operational metric period type."),
        };

    private static ProductionQuantitySourceEntry? ReconstructSource(
        ProductionQuantityEvidenceId sourceId,
        IReadOnlyList<PositionedMetricInputFact> items)
    {
        var produced = SingleOrNull(items, MetricInputFactKeys.PartCountIncrement);
        if (produced is null)
        {
            // Good/rejected evidence without a produced source is not an independently
            // publishable reference-time source.
            return null;
        }

        var good = SingleOrNull(items, MetricInputFactKeys.GoodQuantity);
        var rejected = SingleOrNull(items, MetricInputFactKeys.RejectedQuantity);
        foreach (var sibling in items)
        {
            RequireSameSourceIdentity(produced, sibling);
        }

        var evidence = new ProductionQuantityEvidence
        {
            Id = sourceId,
            CompanyId = produced.Fact.CompanyId,
            SiteId = produced.Fact.SiteId,
            ProductionLineId = produced.Fact.ProductionLineId,
            MachineId = produced.Fact.MachineId,
            ShiftId = produced.Fact.ShiftId,
            ProductionContextAssignmentId = produced.Fact.ProductionContextAssignmentId,
            ProductionOrderId = produced.Fact.ProductionOrderId,
            OperationId = produced.Fact.OperationId,
            PartId = produced.Fact.PartId,
            OperatorId = produced.Fact.OperatorId,
            OccurredAtUtc = produced.Fact.StartsAtUtc,
            PartCountIncrement = ReadQuantity(produced),
            GoodQuantity = good is null ? null : ReadQuantity(good),
            RejectedQuantity = rejected is null ? null : ReadQuantity(rejected),
        };
        evidence.Validate();

        return new ProductionQuantitySourceEntry(
            evidence,
            produced.ShiftOccurrenceId,
            produced.ProductionDayId,
            items.Select(item => item.Position).Distinct().OrderBy(static value => value).ToArray());
    }

    private static PositionedMetricInputFact? SingleOrNull(
        IReadOnlyList<PositionedMetricInputFact> items,
        string key)
    {
        var matches = items.Where(item => string.Equals(item.Fact.Key, key, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Source quantity evidence contains multiple '{key}' contributions at the requested aggregation cut."),
        };
    }

    private static int ReadQuantity(PositionedMetricInputFact item)
    {
        var value = item.Fact.Value;
        if (value < 0 || value != decimal.Truncate(value) || value > int.MaxValue)
        {
            throw new InvalidOperationException(
                "Quantity contribution cannot be represented as canonical non-negative Int32 production evidence.");
        }

        return decimal.ToInt32(value);
    }

    private static void RequireSameSourceIdentity(
        PositionedMetricInputFact canonical,
        PositionedMetricInputFact sibling)
    {
        var first = canonical.Fact;
        var second = sibling.Fact;
        if (first.SourceQuantityEvidenceId != second.SourceQuantityEvidenceId ||
            first.CompanyId != second.CompanyId ||
            first.SiteId != second.SiteId ||
            first.ProductionLineId != second.ProductionLineId ||
            first.MachineId != second.MachineId ||
            first.ShiftId != second.ShiftId ||
            first.ShiftScheduleAssignmentId != second.ShiftScheduleAssignmentId ||
            first.ProductionContextAssignmentId != second.ProductionContextAssignmentId ||
            first.ProductionOrderId != second.ProductionOrderId ||
            first.OperationId != second.OperationId ||
            first.PartId != second.PartId ||
            first.OperatorId != second.OperatorId ||
            first.StartsAtUtc != second.StartsAtUtc ||
            first.EndsAtUtc != second.EndsAtUtc ||
            canonical.ShiftOccurrenceId != sibling.ShiftOccurrenceId ||
            canonical.ProductionDayId != sibling.ProductionDayId)
        {
            throw new InvalidOperationException(
                "Sibling quantity facts with the same source evidence id disagree on canonical identity or period attribution.");
        }
    }
}
