using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed partial class SqlServerMetricAggregationStore
{
    internal async ValueTask<IReadOnlyList<ProductionQuantitySourceEntry>>
        ReadProductionQuantityTransitionSourcesAsync(
            MetricAggregationCheckpoint aggregationCheckpoint,
            MachineId machineId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregationCheckpoint);
        if (aggregationCheckpoint.StreamId.MachineId != machineId)
        {
            throw new ArgumentException(
                "Transition source machine must match the aggregation checkpoint stream machine.",
                nameof(machineId));
        }

        await using var connection = new SqlConnection(_connectionString);
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

        return contributions
            .Where(item => item.Fact.MachineId == machineId)
            .Where(item => item.Fact.SourceQuantityEvidenceId is not null)
            .Where(item => item.Fact.Key is MetricInputFactKeys.PartCountIncrement
                or MetricInputFactKeys.GoodQuantity
                or MetricInputFactKeys.RejectedQuantity)
            .GroupBy(item => item.Fact.SourceQuantityEvidenceId!.Value)
            .Select(group => ReconstructSource(
                group.Key,
                group.OrderBy(item => item.Position).ToArray()))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .OrderBy(entry => entry.ContributingPositions[0])
            .ThenBy(entry => entry.Evidence.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }
}
