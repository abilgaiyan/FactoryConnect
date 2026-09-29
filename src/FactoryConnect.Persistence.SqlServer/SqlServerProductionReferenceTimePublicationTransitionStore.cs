using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Durable compare-and-complete authority for FC-034.2B.3B reference-time publication.
/// The transition row, rather than the legacy paired-cut table by itself, is the
/// authority that says a target aggregation cut has been fully converged.
/// </summary>
public sealed class SqlServerProductionReferenceTimePublicationTransitionStore
{
    private readonly string _connectionString;

    public SqlServerProductionReferenceTimePublicationTransitionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<ProductionReferenceTimePublicationTransition> BeginAsync(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        MetricInputPosition? expectedPreviousAggregationPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);
        ValidatePrevious(targetAggregationPosition, expectedPreviousAggregationPosition);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var processorRowId = await ReadProcessorRowIdAsync(
            connection, transaction, processorId, cancellationToken);
        await RequireAggregationRevisionAsync(
            connection, transaction, processorRowId, targetAggregationPosition, cancellationToken);

        var existing = await ReadAsync(
            connection, transaction, processorId, processorRowId, targetAggregationPosition, cancellationToken);
        if (existing is not null)
        {
            RequireSameExpectedPrevious(existing, expectedPreviousAggregationPosition);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        await RequireExpectedCompletedCutAsync(
            connection,
            transaction,
            processorRowId,
            targetAggregationPosition,
            expectedPreviousAggregationPosition,
            cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO dbo.ProductionReferenceTimePublicationTransition
                    (MetricAggregationProcessorRowId,
                     TargetMetricAggregationPosition,
                     ExpectedPreviousMetricAggregationPosition,
                     ProductionStandardAuthorityRevision,
                     CompletedProductionReferenceTimeRevision,
                     IsCompleted)
                VALUES (@Processor, @Target, @Previous, NULL, NULL, 0);
                """;
            command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", targetAggregationPosition.Value));
            command.Parameters.Add(SqlServerUInt64.CreateNullableParameter(
                "@Previous", expectedPreviousAggregationPosition?.Value));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ProductionReferenceTimePublicationTransition(
            processorId,
            targetAggregationPosition,
            expectedPreviousAggregationPosition,
            null,
            null,
            false);
    }

    /// <summary>
    /// Establishes the one exact production-standard cut for a pending non-empty
    /// publication batch. Repeating the same revision is idempotent; a different
    /// revision fails closed.
    /// </summary>
    public async Task<ProductionReferenceTimePublicationTransition> EstablishStandardRevisionAsync(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        long productionStandardAuthorityRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);
        ArgumentOutOfRangeException.ThrowIfNegative(productionStandardAuthorityRevision);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var processorRowId = await ReadProcessorRowIdAsync(
            connection, transaction, processorId, cancellationToken);
        var transition = await ReadRequiredAsync(
            connection, transaction, processorId, processorRowId, targetAggregationPosition, cancellationToken);

        if (transition.IsCompleted)
        {
            if (transition.ProductionStandardAuthorityRevision != productionStandardAuthorityRevision)
            {
                throw new InvalidOperationException(
                    "A completed reference-time publication transition cannot change its production-standard authority cut.");
            }

            await transaction.CommitAsync(cancellationToken);
            return transition;
        }

        if (transition.ProductionStandardAuthorityRevision is long established)
        {
            if (established != productionStandardAuthorityRevision)
            {
                throw new InvalidOperationException(
                    "A pending reference-time publication transition cannot mix production-standard authority revisions.");
            }

            await transaction.CommitAsync(cancellationToken);
            return transition;
        }

        await RequireStandardRevisionAsync(
            connection, transaction, productionStandardAuthorityRevision, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE dbo.ProductionReferenceTimePublicationTransition
                SET ProductionStandardAuthorityRevision = @Standard
                WHERE MetricAggregationProcessorRowId = @Processor
                  AND TargetMetricAggregationPosition = @Target
                  AND IsCompleted = 0
                  AND ProductionStandardAuthorityRevision IS NULL;
                """;
            command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", targetAggregationPosition.Value));
            command.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@Standard", checked((ulong)productionStandardAuthorityRevision)));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "Reference-time publication transition standard authority changed concurrently.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return transition with
        {
            ProductionStandardAuthorityRevision = productionStandardAuthorityRevision
        };
    }

    public async Task<ProductionReferenceTimePublicationTransition> CompleteAsync(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        ProductionReferenceTimeAuthorityRevision completedReferenceTimeRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var processorRowId = await ReadProcessorRowIdAsync(
            connection, transaction, processorId, cancellationToken);
        var transition = await ReadRequiredAsync(
            connection, transaction, processorId, processorRowId, targetAggregationPosition, cancellationToken);

        if (transition.IsCompleted)
        {
            if (transition.CompletedReferenceTimeRevision != completedReferenceTimeRevision)
            {
                throw new InvalidOperationException(
                    "A completed reference-time publication transition cannot change its completion revision.");
            }

            await transaction.CommitAsync(cancellationToken);
            return transition;
        }

        await RequireReferenceTimeRevisionAsync(
            connection, transaction, processorRowId, completedReferenceTimeRevision, cancellationToken);

        await using (var cut = connection.CreateCommand())
        {
            cut.Transaction = transaction;
            cut.CommandText = """
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.ProductionReferenceTimePublicationCut WITH (UPDLOCK, HOLDLOCK)
                    WHERE MetricAggregationProcessorRowId = @Processor
                      AND MetricAggregationPosition = @Target
                      AND ProductionReferenceTimeRevision = @Revision
                )
                BEGIN
                    INSERT INTO dbo.ProductionReferenceTimePublicationCut
                        (MetricAggregationProcessorRowId,
                         MetricAggregationPosition,
                         ProductionReferenceTimeRevision)
                    VALUES (@Processor, @Target, @Revision);
                END;
                """;
            cut.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            cut.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", targetAggregationPosition.Value));
            cut.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@Revision", checked((ulong)completedReferenceTimeRevision.Value)));
            await cut.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE dbo.ProductionReferenceTimePublicationTransition
                SET CompletedProductionReferenceTimeRevision = @Revision,
                    IsCompleted = 1
                WHERE MetricAggregationProcessorRowId = @Processor
                  AND TargetMetricAggregationPosition = @Target
                  AND IsCompleted = 0;
                """;
            complete.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            complete.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", targetAggregationPosition.Value));
            complete.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@Revision", checked((ulong)completedReferenceTimeRevision.Value)));
            if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "Reference-time publication transition was completed concurrently.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return transition with
        {
            CompletedReferenceTimeRevision = completedReferenceTimeRevision,
            IsCompleted = true
        };
    }

    public async Task<ProductionReferenceTimePublicationTransition?> ReadAsync(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var processorRowId = await ReadProcessorRowIdAsync(
            connection, null, processorId, cancellationToken);
        return await ReadAsync(
            connection, null, processorId, processorRowId, targetAggregationPosition, cancellationToken);
    }

    private static void ValidatePrevious(
        MetricInputPosition target,
        MetricInputPosition? previous)
    {
        if (previous is not null && previous >= target)
        {
            throw new ArgumentException(
                "The expected previous aggregation position must precede the target position.",
                nameof(previous));
        }
    }

    private static void RequireSameExpectedPrevious(
        ProductionReferenceTimePublicationTransition existing,
        MetricInputPosition? expectedPrevious)
    {
        if (existing.ExpectedPreviousAggregationPosition != expectedPrevious)
        {
            throw new InvalidOperationException(
                "Ordinary replay must use the transition's original expected previous publication cut.");
        }
    }

    private static async Task RequireExpectedCompletedCutAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition target,
        MetricInputPosition? expectedPrevious,
        CancellationToken cancellationToken)
    {
        await using var active = connection.CreateCommand();
        active.Transaction = transaction;
        active.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.ProductionReferenceTimePublicationTransition WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND IsCompleted = 0;
            """;
        active.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        if ((long)(await active.ExecuteScalarAsync(cancellationToken))! != 0)
        {
            throw new InvalidOperationException(
                "Only one pending reference-time publication transition may exist for an aggregation processor.");
        }

        if (expectedPrevious is null)
        {
            await using var completed = connection.CreateCommand();
            completed.Transaction = transaction;
            completed.CommandText = """
                SELECT COUNT_BIG(*)
                FROM dbo.ProductionReferenceTimePublicationTransition WITH (UPDLOCK, HOLDLOCK)
                WHERE MetricAggregationProcessorRowId = @Processor
                  AND IsCompleted = 1
                  AND TargetMetricAggregationPosition < @Target;
                """;
            completed.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            completed.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", target.Value));
            if ((long)(await completed.ExecuteScalarAsync(cancellationToken))! != 0)
            {
                throw new InvalidOperationException(
                    "A previous completed transition exists and must be supplied as the expected publication cut.");
            }

            return;
        }

        await using var previous = connection.CreateCommand();
        previous.Transaction = transaction;
        previous.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.ProductionReferenceTimePublicationTransition WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND TargetMetricAggregationPosition = @Previous
              AND IsCompleted = 1;
            """;
        previous.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        previous.Parameters.Add(SqlServerUInt64.CreateParameter("@Previous", expectedPrevious.Value));
        if ((long)(await previous.ExecuteScalarAsync(cancellationToken))! != 1)
        {
            throw new InvalidOperationException(
                "The expected previous reference-time publication transition is not completed.");
        }
    }

    private static async Task<ProductionReferenceTimePublicationTransition> ReadRequiredAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MetricAggregationProcessorId processorId,
        long processorRowId,
        MetricInputPosition target,
        CancellationToken cancellationToken) =>
        await ReadAsync(connection, transaction, processorId, processorRowId, target, cancellationToken)
        ?? throw new InvalidOperationException("Reference-time publication transition is not available.");

    private static async Task<ProductionReferenceTimePublicationTransition?> ReadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        MetricAggregationProcessorId processorId,
        long processorRowId,
        MetricInputPosition target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ExpectedPreviousMetricAggregationPosition,
                   ProductionStandardAuthorityRevision,
                   CompletedProductionReferenceTimeRevision,
                   IsCompleted
            FROM dbo.ProductionReferenceTimePublicationTransition
            WHERE MetricAggregationProcessorRowId = @Processor
              AND TargetMetricAggregationPosition = @Target;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", target.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var previous = reader.IsDBNull(0)
            ? null
            : new MetricInputPosition(checked((ulong)reader.GetDecimal(0)));
        var standard = reader.IsDBNull(1)
            ? (long?)null
            : checked((long)reader.GetDecimal(1));
        ProductionReferenceTimeAuthorityRevision? completed = reader.IsDBNull(2)
            ? null
            : new ProductionReferenceTimeAuthorityRevision(checked((long)reader.GetDecimal(2)));
        return new ProductionReferenceTimePublicationTransition(
            processorId, target, previous, standard, completed, reader.GetBoolean(3));
    }

    private static async Task<long> ReadProcessorRowIdAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        MetricAggregationProcessorId processorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MetricAggregationProcessorRowId
            FROM dbo.MetricAggregationProcessor
            WHERE ProcessorKey = @ProcessorKey;
            """;
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorId.Value;
        return await command.ExecuteScalarAsync(cancellationToken) is long value
            ? value
            : throw new InvalidOperationException("Aggregation authority is not available.");
    }

    private static async Task RequireAggregationRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.MetricAggregationRevision WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND Position = @Position;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1)
        {
            throw new InvalidOperationException("Aggregation revision is not available for reference-time publication.");
        }
    }

    private static async Task RequireStandardRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.ProductionStandardAuthorityRevision WITH (UPDLOCK, HOLDLOCK)
            WHERE ProductionStandardAuthorityRevision = @Revision;
            """;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", checked((ulong)revision)));
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1)
        {
            throw new InvalidOperationException("Production-standard authority revision is not available.");
        }
    }

    private static async Task RequireReferenceTimeRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ProductionReferenceTimeAuthorityRevision revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.ProductionReferenceTimeRevision WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND ProductionReferenceTimeRevision = @Revision;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter(
            "@Revision", checked((ulong)revision.Value)));
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1)
        {
            throw new InvalidOperationException("Reference-time authority revision is not available for completion.");
        }
    }
}
