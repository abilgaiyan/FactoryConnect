using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerObservationIngestionStore :
    IObservationIngestionStore
{
    internal string ConnectionString { get; }

    public SqlServerObservationIngestionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
    }

    public async ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        var streamKeyBinary = OrdinalStringKeyCodec.Encode(
            streamId.StreamKey);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT InstanceId, NextSequence
            FROM dbo.ObservationStreamCheckpoint
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;

        command.Parameters.Add(
            new SqlParameter("@MachineId", SqlDbType.UniqueIdentifier)
            {
                Value = streamId.MachineId.Value,
            });
        command.Parameters.Add(
            new SqlParameter("@StreamKeyBinary", SqlDbType.VarBinary, 512)
            {
                Value = streamKeyBinary,
            });

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var instanceId = SqlServerUInt64.Materialize(
            reader.GetDecimal(0));
        var nextSequence = SqlServerUInt64.Materialize(
            reader.GetDecimal(1));

        return new ObservationCheckpoint(
            streamId,
            instanceId,
            nextSequence);
    }

    public async ValueTask CommitAsync(
        ObservationIngestionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        var staged = StageObservations(batch);
        var streamId = batch.Checkpoint.StreamId;
        var streamKeyBinary = OrdinalStringKeyCodec.Encode(
            streamId.StreamKey);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            var current = await ReadCheckpointAsync(
                connection,
                transaction,
                streamId,
                streamKeyBinary,
                cancellationToken);

            ValidateCheckpointTransition(batch, current);

            await PersistCheckpointAsync(
                connection,
                transaction,
                batch.Checkpoint,
                streamKeyBinary,
                current is null,
                cancellationToken);

            foreach (var observation in staged)
            {
                await InsertObservationAsync(
                    connection,
                    transaction,
                    batch.Checkpoint,
                    streamKeyBinary,
                    observation,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static StagedObservation[] StageObservations(
        ObservationIngestionBatch batch)
    {
        var checkpoint = batch.Checkpoint;
        var result = new StagedObservation[batch.Observations.Count];

        for (var index = 0; index < batch.Observations.Count; index++)
        {
            var item = batch.Observations[index];
            var observation = item.Observation;

            if (observation.MachineId != checkpoint.StreamId.MachineId)
            {
                throw new InvalidOperationException(
                    "Every observation must belong to the checkpoint machine.");
            }

            if (item.Sequence >= checkpoint.NextSequence)
            {
                throw new InvalidOperationException(
                    "Every observation sequence must precede the checkpoint.");
            }

            if (!Enum.IsDefined(observation.Type))
            {
                throw new InvalidOperationException(
                    $"Observation signal type '{observation.Type}' is unsupported.");
            }

            if (!Enum.IsDefined(observation.Quality))
            {
                throw new InvalidOperationException(
                    $"Observation quality '{observation.Quality}' is unsupported.");
            }

            result[index] = new StagedObservation(
                item.Sequence,
                observation,
                SqlServerObservationValueCodec.Serialize(
                    observation.Type,
                    observation.Value));
        }

        return result;
    }

    private static void ValidateCheckpointTransition(
        ObservationIngestionBatch batch,
        ObservationCheckpoint? current)
    {
        var isIdempotentReplay = current == batch.Checkpoint;

        if (!isIdempotentReplay &&
            current != batch.ExpectedCheckpoint)
        {
            throw new InvalidOperationException(
                "The durable checkpoint no longer matches the expected state.");
        }

        if (!isIdempotentReplay &&
            current is not null &&
            current.InstanceId == batch.Checkpoint.InstanceId &&
            batch.Checkpoint.NextSequence < current.NextSequence)
        {
            throw new InvalidOperationException(
                "A checkpoint cannot move backwards within an Agent instance.");
        }
    }

    private static async Task<ObservationCheckpoint?> ReadCheckpointAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationStreamId streamId,
        byte[] streamKeyBinary,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT InstanceId, NextSequence
            FROM dbo.ObservationStreamCheckpoint
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;

        command.Parameters.Add(
            new SqlParameter("@MachineId", SqlDbType.UniqueIdentifier)
            {
                Value = streamId.MachineId.Value,
            });
        command.Parameters.Add(
            new SqlParameter("@StreamKeyBinary", SqlDbType.VarBinary, 512)
            {
                Value = streamKeyBinary,
            });

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ObservationCheckpoint(
            streamId,
            SqlServerUInt64.Materialize(reader.GetDecimal(0)),
            SqlServerUInt64.Materialize(reader.GetDecimal(1)));
    }

    private static async Task PersistCheckpointAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationCheckpoint checkpoint,
        byte[] streamKeyBinary,
        bool insert,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? """
                INSERT INTO dbo.ObservationStreamCheckpoint
                    (MachineId, StreamKeyBinary, StreamKey,
                     InstanceId, NextSequence)
                VALUES
                    (@MachineId, @StreamKeyBinary, @StreamKey,
                     @InstanceId, @NextSequence);
                """
            : """
                UPDATE dbo.ObservationStreamCheckpoint
                SET InstanceId = @InstanceId,
                    NextSequence = @NextSequence
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary;
                """;

        command.Parameters.Add(
            new SqlParameter("@MachineId", SqlDbType.UniqueIdentifier)
            {
                Value = checkpoint.StreamId.MachineId.Value,
            });
        command.Parameters.Add(
            new SqlParameter("@StreamKeyBinary", SqlDbType.VarBinary, 512)
            {
                Value = streamKeyBinary,
            });
        command.Parameters.Add(
            new SqlParameter("@StreamKey", SqlDbType.NVarChar, 256)
            {
                Value = checkpoint.StreamId.StreamKey,
            });
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@InstanceId",
                checkpoint.InstanceId));
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@NextSequence",
                checkpoint.NextSequence));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationCheckpoint checkpoint,
        byte[] streamKeyBinary,
        StagedObservation staged,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.MachineObservation
                (MachineId, StreamKeyBinary, InstanceId, Sequence,
                 Source, Address, SignalType, ObservationValue,
                 Quality, ObservedAt)
            VALUES
                (@MachineId, @StreamKeyBinary, @InstanceId, @Sequence,
                 @Source, @Address, @SignalType, @ObservationValue,
                 @Quality, @ObservedAt);
            """;

        command.Parameters.Add(
            new SqlParameter("@MachineId", SqlDbType.UniqueIdentifier)
            {
                Value = checkpoint.StreamId.MachineId.Value,
            });
        command.Parameters.Add(
            new SqlParameter("@StreamKeyBinary", SqlDbType.VarBinary, 512)
            {
                Value = streamKeyBinary,
            });
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@InstanceId",
                checkpoint.InstanceId));
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@Sequence",
                staged.Sequence));
        command.Parameters.Add(
            new SqlParameter("@Source", SqlDbType.NVarChar, 256)
            {
                Value = staged.Observation.Source,
            });
        command.Parameters.Add(
            new SqlParameter("@Address", SqlDbType.NVarChar, 512)
            {
                Value = staged.Observation.Address,
            });
        command.Parameters.Add(
            new SqlParameter("@SignalType", SqlDbType.TinyInt)
            {
                Value = (byte)staged.Observation.Type,
            });
        command.Parameters.Add(
            new SqlParameter("@ObservationValue", SqlDbType.NVarChar, -1)
            {
                Value = staged.SerializedValue is null
                    ? DBNull.Value
                    : staged.SerializedValue,
            });
        command.Parameters.Add(
            new SqlParameter("@Quality", SqlDbType.TinyInt)
            {
                Value = (byte)staged.Observation.Quality,
            });
        command.Parameters.Add(
            new SqlParameter("@ObservedAt", SqlDbType.DateTimeOffset)
            {
                Value = staged.Observation.Timestamp,
            });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record StagedObservation(
        ulong Sequence,
        MachineObservation Observation,
        string? SerializedValue);
}
