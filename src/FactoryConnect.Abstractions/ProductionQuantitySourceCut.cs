namespace FactoryConnect.Abstractions;

/// <summary>One canonical produced-quantity source reconstructed from the durable FC-026 contribution ledger.</summary>
public sealed record ProductionQuantitySourceEntry(
    ProductionQuantityEvidence Evidence,
    ShiftOccurrenceId ShiftOccurrenceId,
    ProductionDayId ProductionDayId,
    IReadOnlyList<MetricInputPosition> ContributingPositions);

/// <summary>The complete produced-quantity source inventory visible through one exact aggregation checkpoint.</summary>
public sealed record ProductionQuantitySourceCut(
    MetricAggregationCheckpoint AggregationCheckpoint,
    OperationalMetricPeriodId PeriodId,
    IReadOnlyList<ProductionQuantitySourceEntry> Sources);

public interface IProductionQuantitySourceCutReader
{
    ValueTask<ProductionQuantitySourceCut> ReadProductionQuantitySourceCutAsync(
        MetricAggregationCheckpoint aggregationCheckpoint,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        CancellationToken cancellationToken = default);
}
