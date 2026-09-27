using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed partial class SqlServerMetricAggregationStore
{
    private static async Task PublishEmptyReferenceTimeCutAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF NOT EXISTS (
                SELECT 1
                FROM dbo.ProductionReferenceTimeRevision WITH (UPDLOCK, HOLDLOCK)
                WHERE MetricAggregationProcessorRowId = @ProcessorRowId
                  AND ProductionReferenceTimeRevision = 0
            )
            BEGIN
                INSERT INTO dbo.ProductionReferenceTimeRevision
                    (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
                VALUES (@ProcessorRowId, 0);
            END;

            INSERT INTO dbo.ProductionReferenceTimePublicationCut
                (MetricAggregationProcessorRowId, MetricAggregationPosition, ProductionReferenceTimeRevision)
            VALUES (@ProcessorRowId, @Position, 0);
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
