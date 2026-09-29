using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Durable FC-034.2B.3B transition authority. Claim creation fixes Aprev, A,
/// optional S, and StartingR before source admission. Completion proves exact
/// delta coverage and publishes the terminal R in one serializable transaction.
/// </summary>
public sealed class SqlServerProductionReferenceTimePublicationTransitionStore
{
    private const string ProducedQuantityMetricKey = "quantity.part-count-increment";
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
        ProductionReferenceTimeAuthorityRevision startingReferenceTimeRevision,
        long? productionStandardAuthorityRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);
        ValidatePrevious(targetAggregationPosition, expectedPreviousAggregationPosition);
        if (productionStandardAuthorityRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(productionStandardAuthorityRevision));
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var processorRowId = await ReadProcessorRowIdAsync(
            connection,
            transaction,
            processorId,
            cancellationToken);

        var existing = await ReadAsync(
            connection,
            transaction,
            processorId,
            processorRowId,
            targetAggregationPosition,
            forUpdate: true,
            cancellationToken);
        if (existing is not null)
        {
            RequireEquivalentClaim(
                existing,
                expectedPreviousAggregationPosition,
                startingReferenceTimeRevision,
                productionStandardAuthorityRevision);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        await RequireNoPendingTransitionAsync(connection, transaction, processorRowId, cancellationToken);
        await RequireLatestCompletedPredecessorAsync(
            connection,
            transaction,
            processorRowId,
            expectedPreviousAggregationPosition,
            cancellationToken);
        await RequireNextAggregationRevisionAsync(
            connection,
            transaction,
            processorRowId,
            expectedPreviousAggregationPosition,
            targetAggregationPosition,
            cancellationToken);
        await RequireLatestReferenceTimeRevisionAsync(
            connection,
            transaction,
            processorRowId,
            startingReferenceTimeRevision,
            cancellationToken);

        var expectedSources = await ReadExpectedSourceIdsAsync(
            connection,
            transaction,
            processorRowId,
            expectedPreviousAggregationPosition,
            targetAggregationPosition,
            cancellationToken);
        if (expectedSources.Count == 0)
        {
            if (productionStandardAuthorityRevision is not null)
            {
                throw new InvalidOperationException(
                    "An empty reference-time publication delta must not claim a production-standard authority revision.");
            }
        }
        else
        {
            if (productionStandardAuthorityRevision is null)
            {
                throw new InvalidOperationException(
                    "A non-empty reference-time publication delta must durably claim one production-standard authority revision before admission.");
            }

            await RequireStandardRevisionAsync(
                connection,
                transaction,
                productionStandardAuthorityRevision.Value,
                cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO dbo.ProductionReferenceTimePublicationTransition
                    (MetricAggregationProcessorRowId,
                     TargetMetricAggregationPosition,
                     ExpectedPreviousMetricAggregationPosition,
                     ProductionStandardAuthorityRevision,
                     StartingProductionReferenceTimeRevision,
                     State,
                     FinalProductionReferenceTimeRevision)
                VALUES
                    (@Processor, @Target, @Previous, @Standard, @StartingR, 0, NULL);
                """;
            command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", targetAggregationPosition.Value));
            command.Parameters.Add(SqlServerUInt64.CreateNullableParameter(
                "@Previous",
                expectedPreviousAggregationPosition?.Value));
            command.Parameters.Add(SqlServerUInt64.CreateNullableParameter(
                "@Standard",
                productionStandardAuthorityRevision is long standard
                    ? checked((ulong)standard)
                    : null));
            command.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@StartingR",
                checked((ulong)startingReferenceTimeRevision.Value)));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ProductionReferenceTimePublicationTransition(
            processorId,
            targetAggregationPosition,
            expectedPreviousAggregationPosition,
            productionStandardAuthorityRevision,
            startingReferenceTimeRevision,
            null,
            false);
    }

    public async Task<ProductionReferenceTimePublicationTransition> CompleteAsync(
        MetricAggregationProcessorId processorId,
        MetricInputPosition targetAggregationPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(targetAggregationPosition);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var processorRowId = await ReadProcessorRowIdAsync(
            connection,
            transaction,
            processorId,
            cancellationToken);
        var transition = await ReadRequiredAsync(
            connection,
            transaction,
            processorId,
            processorRowId,
            targetAggregationPosition,
            forUpdate: true,
            cancellationToken);

        if (transition.IsCompleted)
        {
            await transaction.CommitAsync(cancellationToken);
            return transition;
        }

        var expectedSources = await ReadExpectedSourceIdsAsync(
            connection,
            transaction,
            processorRowId,
            transition.ExpectedPreviousAggregationPosition,
            targetAggregationPosition,
            cancellationToken);
        var finalRevision = await ReadLatestReferenceTimeRevisionAsync(
            connection,
            transaction,
            processorRowId,
            cancellationToken);
        var admittedSources = await ReadAdmittedSourceIdsAsync(
            connection,
            transaction,
            processorRowId,
            transition.StartingReferenceTimeRevision,
            finalRevision,
            cancellationToken);

        if (!expectedSources.SetEquals(admittedSources))
        {
            throw new InvalidOperationException(
                "Reference-time publication cannot complete because exact delta source coverage has not been proven.");
        }

        if (expectedSources.Count == 0)
        {
            if (transition.ProductionStandardAuthorityRevision is not null ||
                finalRevision != transition.StartingReferenceTimeRevision)
            {
                throw new InvalidOperationException(
                    "An empty reference-time publication transition must complete without a standard cut or reference-time revision advance.");
            }
        }
        else
        {
            if (transition.ProductionStandardAuthorityRevision is not long standardRevision)
            {
                throw new InvalidOperationException(
                    "A non-empty reference-time publication transition has no durable production-standard authority revision.");
            }

            if (finalRevision.Value <= transition.StartingReferenceTimeRevision.Value)
            {
                throw new InvalidOperationException(
                    "A non-empty reference-time publication transition must advance reference-time authority.");
            }

            await RequireAdmittedStandardRevisionAsync(
                connection,
                transaction,
                processorRowId,
                transition.StartingReferenceTimeRevision,
                finalRevision,
                standardRevision,
                cancellationToken);
        }

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
                "@Revision",
                checked((ulong)finalRevision.Value)));
            await cut.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE dbo.ProductionReferenceTimePublicationTransition
                SET State = 1,
                    FinalProductionReferenceTimeRevision = @FinalR
                WHERE MetricAggregationProcessorRowId = @Processor
                  AND TargetMetricAggregationPosition = @Target
                  AND State = 0;
                """;
            complete.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
            complete.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", targetAggregationPosition.Value));
            complete.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@FinalR",
                checked((ulong)finalRevision.Value)));
            if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "Reference-time publication transition completion raced with another authority operation.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return transition with
        {
            CompletedReferenceTimeRevision = finalRevision,
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
            connection,
            null,
            processorId,
            cancellationToken);
        return await ReadAsync(
            connection,
            null,
            processorId,
            processorRowId,
            targetAggregationPosition,
            forUpdate: false,
            cancellationToken);
    }

    private static void ValidatePrevious(MetricInputPosition target, MetricInputPosition? previous)
    {
        if (previous is not null && previous >= target)
        {
            throw new ArgumentException(
                "The expected previous aggregation position must precede the target position.",
                nameof(previous));
        }
    }

    private static void RequireEquivalentClaim(
        ProductionReferenceTimePublicationTransition existing,
        MetricInputPosition? expectedPrevious,
        ProductionReferenceTimeAuthorityRevision startingRevision,
        long? standardRevision)
    {
        if (existing.ExpectedPreviousAggregationPosition != expectedPrevious ||
            existing.StartingReferenceTimeRevision != startingRevision ||
            existing.ProductionStandardAuthorityRevision != standardRevision)
        {
            throw new InvalidOperationException(
                "Ordinary claim replay must use the exact durable Aprev, A, S, and StartingR identity.");
        }
    }

    private static async Task RequireNoPendingTransitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.ProductionReferenceTimePublicationTransition WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND State = 0;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
        {
            throw new InvalidOperationException(
                "Only one pending reference-time publication transition may exist for an aggregation processor.");
        }
    }

    private static async Task RequireLatestCompletedPredecessorAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition? expectedPrevious,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TOP (1) TargetMetricAggregationPosition
            FROM dbo.ProductionReferenceTimePublicationTransition WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND State = 1
            ORDER BY TargetMetricAggregationPosition DESC;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        MetricInputPosition? latestCompleted = scalar is decimal value
            ? new MetricInputPosition(checked((ulong)value))
            : null;
        if (latestCompleted != expectedPrevious)
        {
            throw new InvalidOperationException(
                "Reference-time transition predecessor must be the latest completed transition for the processor.");
        }
    }

    private static async Task RequireNextAggregationRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition? previous,
        MetricInputPosition target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = previous is null
            ? """
                SELECT MIN(Position)
                FROM dbo.MetricAggregationRevision WITH (UPDLOCK, HOLDLOCK)
                WHERE MetricAggregationProcessorRowId = @Processor;
                """
            : """
                SELECT MIN(Position)
                FROM dbo.MetricAggregationRevision WITH (UPDLOCK, HOLDLOCK)
                WHERE MetricAggregationProcessorRowId = @Processor
                  AND Position > @Previous;
                """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        if (previous is not null)
        {
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Previous", previous.Value));
        }

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is not decimal value || checked((ulong)value) != target.Value)
        {
            throw new InvalidOperationException(
                "Reference-time transition target must be the next exact aggregation revision after the latest completed predecessor.");
        }
    }

    private static async Task RequireLatestReferenceTimeRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ProductionReferenceTimeAuthorityRevision expected,
        CancellationToken cancellationToken)
    {
        var latest = await ReadLatestReferenceTimeRevisionAsync(
            connection,
            transaction,
            processorRowId,
            cancellationToken);
        if (latest != expected)
        {
            throw new InvalidOperationException(
                "Reference-time transition StartingR must equal the latest durable reference-time revision while the claim is created.");
        }
    }

    internal static async Task<HashSet<string>> ReadExpectedSourceIdsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition? previous,
        MetricInputPosition target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = previous is null
            ? """
                SELECT DISTINCT f.SourceQuantityEvidenceId
                FROM dbo.MetricAggregationContribution c WITH (UPDLOCK, HOLDLOCK)
                JOIN dbo.MetricInputFact f
                  ON f.MetricInputFactRowId = c.MetricInputFactRowId
                 AND f.MetricInputStreamRowId = c.MetricInputStreamRowId
                 AND f.Position = c.Position
                WHERE c.MetricAggregationProcessorRowId = @Processor
                  AND c.Position <= @Target
                  AND f.MetricInputKey = @MetricKey
                  AND f.SourceQuantityEvidenceId IS NOT NULL;
                """
            : """
                SELECT DISTINCT f.SourceQuantityEvidenceId
                FROM dbo.MetricAggregationContribution c WITH (UPDLOCK, HOLDLOCK)
                JOIN dbo.MetricInputFact f
                  ON f.MetricInputFactRowId = c.MetricInputFactRowId
                 AND f.MetricInputStreamRowId = c.MetricInputStreamRowId
                 AND f.Position = c.Position
                WHERE c.MetricAggregationProcessorRowId = @Processor
                  AND c.Position > @Previous
                  AND c.Position <= @Target
                  AND f.MetricInputKey = @MetricKey
                  AND f.SourceQuantityEvidenceId IS NOT NULL;
                """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Target", target.Value));
        if (previous is not null)
        {
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Previous", previous.Value));
        }
        command.Parameters.Add("@MetricKey", SqlDbType.NVarChar, 256).Value = ProducedQuantityMetricKey;

        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadAdmittedSourceIdsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ProductionReferenceTimeAuthorityRevision starting,
        ProductionReferenceTimeAuthorityRevision final,
        CancellationToken cancellationToken)
    {
        if (final.Value <= starting.Value)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SourceQuantityEvidenceId
            FROM dbo.ProductionReferenceTimeOutcome WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND ProductionReferenceTimeRevision > @StartingR
              AND ProductionReferenceTimeRevision <= @FinalR;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@StartingR", checked((ulong)starting.Value)));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@FinalR", checked((ulong)final.Value)));

        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task RequireAdmittedStandardRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ProductionReferenceTimeAuthorityRevision starting,
        ProductionReferenceTimeAuthorityRevision final,
        long expectedStandardRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.ProductionReferenceTimeOutcome WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor
              AND ProductionReferenceTimeRevision > @StartingR
              AND ProductionReferenceTimeRevision <= @FinalR
              AND ProductionStandardAuthorityRevision <> @Standard;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@StartingR", checked((ulong)starting.Value)));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@FinalR", checked((ulong)final.Value)));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Standard", checked((ulong)expectedStandardRevision)));
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
        {
            throw new InvalidOperationException(
                "Reference-time publication transition contains outcomes from a different production-standard authority revision.");
        }
    }

    private static async Task<ProductionReferenceTimePublicationTransition> ReadRequiredAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MetricAggregationProcessorId processorId,
        long processorRowId,
        MetricInputPosition target,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        await ReadAsync(connection, transaction, processorId, processorRowId, target, forUpdate, cancellationToken)
        ?? throw new InvalidOperationException("Reference-time publication transition is not available.");

    private static async Task<ProductionReferenceTimePublicationTransition?> ReadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        MetricAggregationProcessorId processorId,
        long processorRowId,
        MetricInputPosition target,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var lockHint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText = $"""
            SELECT ExpectedPreviousMetricAggregationPosition,
                   ProductionStandardAuthorityRevision,
                   StartingProductionReferenceTimeRevision,
                   State,
                   FinalProductionReferenceTimeRevision
            FROM dbo.ProductionReferenceTimePublicationTransition{lockHint}
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

        MetricInputPosition? previous = reader.IsDBNull(0)
            ? null
            : new MetricInputPosition(checked((ulong)reader.GetDecimal(0)));
        long? standard = reader.IsDBNull(1)
            ? null
            : checked((long)reader.GetDecimal(1));
        var starting = new ProductionReferenceTimeAuthorityRevision(checked((long)reader.GetDecimal(2)));
        var state = reader.GetByte(3);
        ProductionReferenceTimeAuthorityRevision? final = reader.IsDBNull(4)
            ? null
            : new ProductionReferenceTimeAuthorityRevision(checked((long)reader.GetDecimal(4)));
        return new ProductionReferenceTimePublicationTransition(
            processorId,
            target,
            previous,
            standard,
            starting,
            final,
            state == 1);
    }

    internal static async Task<long> ReadProcessorRowIdAsync(
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

    internal static async Task<ProductionReferenceTimeAuthorityRevision> ReadLatestReferenceTimeRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(ProductionReferenceTimeRevision)
            FROM dbo.ProductionReferenceTimeRevision WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @Processor;
            """;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processorRowId;
        return await command.ExecuteScalarAsync(cancellationToken) is decimal value
            ? new ProductionReferenceTimeAuthorityRevision(checked((long)value))
            : throw new InvalidOperationException("Reference-time authority is not initialized for the aggregation processor.");
    }
}
