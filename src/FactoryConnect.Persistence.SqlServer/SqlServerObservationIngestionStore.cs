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

        AddStreamIdentityParameters(command, streamId, streamKeyBinary);

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
            var current = await ReadCheckpointForUpdateAsync(
                connection,
                transaction,
                streamId,
                streamKeyBinary,
                cancellationToken);

            var isIdempotentReplay = current == batch.Checkpoint;
            ValidateCheckpointTransition(
                batch,
                current,
                isIdempotentReplay);

            var pending = await ReconcileObservationsAsync(
                connection,
                transaction,
                batch.Checkpoint,
                streamKeyBinary,
                staged,
                isIdempotentReplay,
                cancellationToken);

            if (!isIdempotentReplay)
            {
                await PersistCheckpointAsync(
                    connection,
                    transaction,
                    batch.Checkpoint,
                    streamKeyBinary,
                    current is null,
                    cancellationToken);
            }

            foreach (var observation in pending)
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
        Dictionary<ulong, StagedObservation> staged = [];

        foreach (var item in batch.Observations)
        {
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

            var candidate = new StagedObservation(
                item.Sequence,
                observation,
                SqlServerObservationValueCodec.Serialize(
                    observation.Type,
                    observation.Value));

            if (staged.TryGetValue(item.Sequence, out var existing))
            {
                if (!SqlServerObservationEquivalence.AreEquivalent(
                        existing.Observation,
                        candidate.Observation))
                {
                    throw new InvalidOperationException(
                        "The batch contains different observations at the " +
                        "same instance and sequence.");
                }

                continue;
            }

            staged.Add(item.Sequence, candidate);
        }

        return [.. staged.Values];
    }

    private static void ValidateCheckpointTransition(
        ObservationIngestionBatch batch,
        ObservationCheckpoint? current,
        bool isIdempotentReplay)
    {
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

    private static async Task<ObservationCheckpoint?>
        ReadCheckpointForUpdateAsync(
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
            FROM dbo.ObservationStreamCheckpoint WITH (UPDLOCK, HOLDLOCK)
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;

        AddStreamIdentityParameters(command, streamId, streamKeyBinary);

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

    private static async Task<StagedObservation[]> ReconcileObservationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationCheckpoint checkpoint,
        byte[] streamKeyBinary,
        IReadOnlyList<StagedObservation> staged,
        bool isIdempotentReplay,
        CancellationToken cancellationToken)
    {
        List<StagedObservation> pending = [];

        foreach (var candidate in staged)
        {
            var existing = await ReadObservationAsync(
                connection,
                transaction,
                checkpoint,
                streamKeyBinary,
                candidate.Sequence,
                cancellationToken);

            if (existing is null)
            {
                if (isIdempotentReplay)
                {
                    throw new InvalidOperationException(
                        "An idempotent replay cannot add observations to an " +
                        "already committed checkpoint.");
                }

                pending.Add(candidate);
                continue;
            }

            if (!SqlServerObservationEquivalence.AreEquivalent(
                    existing,
                    candidate.Observation))
            {
                throw new InvalidOperationException(
                    "The stream already contains a different observation " +
                    "at the same instance and sequence.");
            }
        }

        return [.. pending];
    }

    private static async Task<MachineObservation?> ReadObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationCheckpoint checkpoint,
        byte[] streamKeyBinary,
        ulong sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Source, Address, SignalType, ObservationValue,
                   Quality, ObservedAt
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND InstanceId = @InstanceId
              AND Sequence = @Sequence;
            """;

        AddStreamIdentityParameters(
            command,
            checkpoint.StreamId,
            streamKeyBinary);
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@InstanceId",
                checkpoint.InstanceId));
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@Sequence",
                sequence));

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var type = (SignalType)reader.GetByte(2);
        var quality = (ObservationQuality)reader.GetByte(4);
        var persistedValue = reader.IsDBNull(3)
            ? null
            : reader.GetString(3);

        if (!Enum.IsDefined(type) || !Enum.IsDefined(quality))
        {
            throw new InvalidDataException(
                "Persisted observation contains an unsupported enum value.");
        }

        return new MachineObservation
        {
            MachineId = checkpoint.StreamId.MachineId,
            Source = reader.GetString(0),
            Address = reader.GetString(1),
            Type = type,
            Value = SqlServerObservationValueCodec.Deserialize(
                type,
                persistedValue),
            Quality = quality,
            Timestamp = reader.GetDateTimeOffset(5),
        };
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

        AddStreamIdentityParameters(
            command,
            checkpoint.StreamId,
            streamKeyBinary);
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

        AddStreamIdentityParameters(
            command,
            checkpoint.StreamId,
            streamKeyBinary);
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

    private static void AddStreamIdentityParameters(
        SqlCommand command,
        ObservationStreamId streamId,
        byte[] streamKeyBinary)
    {
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
    }

    private sealed record StagedObservation(
        ulong Sequence,
        MachineObservation Observation,
        string? SerializedValue);
}
