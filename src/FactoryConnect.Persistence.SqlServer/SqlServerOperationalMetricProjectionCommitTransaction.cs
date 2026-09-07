using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlServerOperationalMetricProjectionCommitMode
{
    Advance,
    ReconcileProposed,
}

internal sealed class SqlServerOperationalMetricProjectionCommitContext
{
    public SqlServerOperationalMetricProjectionCommitContext(
        SqlConnection connection,
        SqlTransaction transaction,
        long projectionProcessorRowId,
        SqlServerOperationalMetricProjectionCommitMode mode,
        MetricInputPosition? durablePosition)
    {
        Connection = connection;
        Transaction = transaction;
        ProjectionProcessorRowId = projectionProcessorRowId;
        Mode = mode;
        DurablePosition = durablePosition;
    }

    public SqlConnection Connection { get; }

    public SqlTransaction Transaction { get; }

    public long ProjectionProcessorRowId { get; }

    public SqlServerOperationalMetricProjectionCommitMode Mode { get; }

    public MetricInputPosition? DurablePosition { get; }
}

internal sealed record SqlServerOperationalMetricProjectionCheckpointHeader(
    long ProjectionProcessorRowId,
    long MetricAggregationProcessorRowId,
    long MetricInputStreamRowId,
    MetricInputPosition Position);

internal sealed class SqlServerOperationalMetricProjectionCommitTransaction
{
    private readonly string _connectionString;

    public SqlServerOperationalMetricProjectionCommitTransaction(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<SqlServerOperationalMetricProjectionCheckpointHeader?> ReadCheckpointHeaderAsync(
        OperationalMetricProjectionProcessorId processorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);

        var keyBinary = StringOrderKeyV2Codec.Encode(processorId.Value);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.OperationalMetricProjectionProcessorRowId, " +
            "p.MetricAggregationProcessorRowId, p.MetricInputStreamRowId, c.Position, " +
            "p.ProcessorKey, p.ProcessorKeyBinary " +
            "FROM dbo.OperationalMetricProjectionProcessor AS p " +
            "LEFT JOIN dbo.OperationalMetricProjectionCheckpoint AS c " +
            "ON c.OperationalMetricProjectionProcessorRowId = p.OperationalMetricProjectionProcessorRowId " +
            "WHERE p.ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
            .Value = keyBinary;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        ValidateProcessorIdentity(reader.GetString(4), (byte[])reader[5], processorId, keyBinary);

        if (reader.IsDBNull(3))
        {
            return null;
        }

        return new SqlServerOperationalMetricProjectionCheckpointHeader(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            new MetricInputPosition(SqlServerUInt64.Materialize(reader.GetDecimal(3))));
    }

    public async Task ExecuteAsync(
        OperationalMetricProjectionCommit commit,
        Func<SqlServerOperationalMetricProjectionCommitContext, CancellationToken, Task> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(body);

        var processorKeyBinary = StringOrderKeyV2Codec.Encode(commit.ProcessorId.Value);
        var sourceBinding = await ResolveSourceBindingAsync(
            commit.ProposedCheckpoint.SourceRevision,
            cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var processor = await LockProcessorSlotAsync(
                connection,
                transaction,
                commit.ProcessorId,
                processorKeyBinary,
                cancellationToken);

            var processorRowId = processor is null
                ? await InsertProcessorAsync(
                    connection,
                    transaction,
                    commit.ProcessorId,
                    processorKeyBinary,
                    sourceBinding,
                    cancellationToken)
                : ValidateBindingAndGetRowId(processor.Value, sourceBinding);

            var durablePosition = await LockCheckpointSlotAsync(
                connection,
                transaction,
                processorRowId,
                cancellationToken);

            var mode = ClassifyRevision(commit, durablePosition);
            var context = new SqlServerOperationalMetricProjectionCommitContext(
                connection,
                transaction,
                processorRowId,
                mode,
                durablePosition);

            // C.3/C.4 continue the frozen lock order from here:
            // complete projection identity set -> manifest -> evidence.
            await body(context, cancellationToken);

            if (mode == SqlServerOperationalMetricProjectionCommitMode.Advance)
            {
                await WriteCheckpointAsync(
                    connection,
                    transaction,
                    processorRowId,
                    durablePosition,
                    commit.ProposedCheckpoint.SourceRevision.Position,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the primary persistence/cancellation failure.
            }

            throw;
        }
    }

    private async Task<SourceBinding> ResolveSourceBindingAsync(
        MetricAggregationCheckpoint sourceRevision,
        CancellationToken cancellationToken)
    {
        var processorKeyBinary = OrdinalStringKeyCodec.Encode(sourceRevision.ProcessorId.Value);
        var streamKeyBinary = OrdinalStringKeyCodec.Encode(sourceRevision.StreamId.StreamKey);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.MetricAggregationProcessorRowId, p.MetricInputStreamRowId, p.ProcessorKey, " +
            "s.MachineId, s.StreamKey " +
            "FROM dbo.MetricAggregationProcessor AS p " +
            "INNER JOIN dbo.MetricInputStream AS s " +
            "ON s.MetricInputStreamRowId = p.MetricInputStreamRowId " +
            "WHERE p.ProcessorKeyBinary = @ProcessorKeyBinary " +
            "AND s.MachineId = @MachineId " +
            "AND s.StreamKeyBinary = @StreamKeyBinary;";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, 512).Value = processorKeyBinary;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = sourceRevision.StreamId.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value = streamKeyBinary;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Projection source aggregation processor and metric input stream are not durably bound.");
        }

        if (!string.Equals(reader.GetString(2), sourceRevision.ProcessorId.Value, StringComparison.Ordinal) ||
            reader.GetGuid(3) != sourceRevision.StreamId.MachineId.Value ||
            !string.Equals(reader.GetString(4), sourceRevision.StreamId.StreamKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Persisted projection source binding does not match its canonical identity.");
        }

        return new SourceBinding(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<ProjectionProcessorRow?> LockProcessorSlotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalMetricProjectionProcessorId processorId,
        byte[] processorKeyBinary,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT OperationalMetricProjectionProcessorRowId, MetricAggregationProcessorRowId, " +
            "MetricInputStreamRowId, ProcessorKey, ProcessorKeyBinary " +
            "FROM dbo.OperationalMetricProjectionProcessor WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
            .Value = processorKeyBinary;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        ValidateProcessorIdentity(reader.GetString(3), (byte[])reader[4], processorId, processorKeyBinary);
        return new ProjectionProcessorRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> InsertProcessorAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperationalMetricProjectionProcessorId processorId,
        byte[] processorKeyBinary,
        SourceBinding sourceBinding,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO dbo.OperationalMetricProjectionProcessor " +
            "(ProcessorKeyBinary, ProcessorKey, MetricAggregationProcessorRowId, MetricInputStreamRowId) " +
            "OUTPUT INSERTED.OperationalMetricProjectionProcessorRowId " +
            "VALUES (@ProcessorKeyBinary, @ProcessorKey, @MetricAggregationProcessorRowId, @MetricInputStreamRowId);";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
            .Value = processorKeyBinary;
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorId.Value;
        command.Parameters.Add("@MetricAggregationProcessorRowId", SqlDbType.BigInt).Value = sourceBinding.ProcessorRowId;
        command.Parameters.Add("@MetricInputStreamRowId", SqlDbType.BigInt).Value = sourceBinding.StreamRowId;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static long ValidateBindingAndGetRowId(
        ProjectionProcessorRow processor,
        SourceBinding sourceBinding)
    {
        if (processor.MetricAggregationProcessorRowId != sourceBinding.ProcessorRowId ||
            processor.MetricInputStreamRowId != sourceBinding.StreamRowId)
        {
            throw new InvalidOperationException(
                "Operational metric projection processor cannot change its source aggregation processor or metric input stream binding.");
        }

        return processor.RowId;
    }

    private static async Task<MetricInputPosition?> LockCheckpointSlotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Position FROM dbo.OperationalMetricProjectionCheckpoint WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : new MetricInputPosition(SqlServerUInt64.Materialize((decimal)result));
    }

    private static SqlServerOperationalMetricProjectionCommitMode ClassifyRevision(
        OperationalMetricProjectionCommit commit,
        MetricInputPosition? durablePosition)
    {
        var expected = commit.ExpectedCheckpoint?.SourceRevision.Position;
        var proposed = commit.ProposedCheckpoint.SourceRevision.Position;

        if (durablePosition is null)
        {
            if (expected is not null)
            {
                throw new InvalidOperationException("Operational metric projection checkpoint conflict.");
            }

            return SqlServerOperationalMetricProjectionCommitMode.Advance;
        }

        if (durablePosition == proposed)
        {
            return SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed;
        }

        if (proposed < durablePosition)
        {
            throw new InvalidOperationException(
                "Operational metric projection source revision cannot move backward.");
        }

        if (expected is null || durablePosition != expected)
        {
            throw new InvalidOperationException("Operational metric projection checkpoint conflict.");
        }

        return SqlServerOperationalMetricProjectionCommitMode.Advance;
    }

    private static async Task WriteCheckpointAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition? current,
        MetricInputPosition proposed,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (current is null)
        {
            command.CommandText =
                "INSERT INTO dbo.OperationalMetricProjectionCheckpoint " +
                "(OperationalMetricProjectionProcessorRowId, Position) VALUES (@ProcessorRowId, @Position);";
        }
        else
        {
            command.CommandText =
                "UPDATE dbo.OperationalMetricProjectionCheckpoint SET Position = @Position " +
                "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId AND Position = @ExpectedPosition;";
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@ExpectedPosition", current.Value));
        }

        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", proposed.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Operational metric projection checkpoint conflict.");
        }
    }

    private static void ValidateProcessorIdentity(
        string persistedKey,
        byte[] persistedKeyBinary,
        OperationalMetricProjectionProcessorId processorId,
        byte[] expectedKeyBinary)
    {
        if (!string.Equals(persistedKey, processorId.Value, StringComparison.Ordinal) ||
            !persistedKeyBinary.AsSpan().SequenceEqual(expectedKeyBinary))
        {
            throw new InvalidOperationException(
                "Persisted operational metric projection processor key is inconsistent with StringOrderKeyV2.");
        }
    }

    private readonly record struct SourceBinding(long ProcessorRowId, long StreamRowId);

    private readonly record struct ProjectionProcessorRow(
        long RowId,
        long MetricAggregationProcessorRowId,
        long MetricInputStreamRowId);
}
