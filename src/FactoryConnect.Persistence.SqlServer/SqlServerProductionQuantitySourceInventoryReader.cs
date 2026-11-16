using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Read-only historical source inventory. Ledger inclusion alone does not qualify
/// quantities as accepted production or establish complete shift coverage.
/// </summary>
public sealed class SqlServerProductionQuantitySourceInventoryReader
{
    private readonly SqlServerMetricAggregationStore _store;

    public SqlServerProductionQuantitySourceInventoryReader(string connectionString)
    {
        _store = new SqlServerMetricAggregationStore(connectionString);
    }

    public ValueTask<ProductionQuantitySourceCut> ReadAsync(
        MetricAggregationCheckpoint checkpoint,
        MachineId machineId,
        ShiftOccurrenceId occurrence,
        CancellationToken cancellationToken = default) =>
        _store.ReadProductionQuantitySourceCutAsync(
            checkpoint, machineId, new OperationalMetricPeriodId.Shift(occurrence),
            cancellationToken);
}
