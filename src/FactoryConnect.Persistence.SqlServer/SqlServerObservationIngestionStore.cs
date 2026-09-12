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

        var streamKeyBinary = OrdinalStringKeyCodec.Encode(streamId.StreamKey);

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

    public async ValueTask<AcquisitionContactAuthority?>
        ReadAcquisitionContactAuthorityAsync(
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        var streamKeyBinary = OrdinalStringKeyCodec.Encode(streamId.StreamKey);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return await ReadAcquisitionContactAuthorityAsync(
            connection,
            transaction: null,
            streamId,
            streamKeyBinary,
            cancellationToken);
    }

    public async ValueTask CommitAsync(
        ObservationIngestionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        var staged = StageObservations(batch);
        var streamId = batch.Checkpoint.StreamId;
        var streamKeyBinary = OrdinalStringKeyCodec.Encode(streamId.StreamKey);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var current = await ReadCheckpointForUpdateAsync(
                connection,
                transaction,
                streamId,
                streamKeyBinary,
                cancellationToken);

            var currentAuthority = await ReadAcquisitionContactAuthorityAsync(
                connection,
                transaction,
                streamId,
                streamKeyBinary,
                cancellationToken);

            var currentRawAcceptedThrough = await ReadRawAcceptedThroughAsync(
                connection,
                transaction,
                streamId,
                streamKeyBinary,
                cancellationToken);

            var casAuthorized = current == batch.ExpectedCheckpoint;
            if (!casAuthorized)
            {
                await ValidateExactCurrentStateReplayAsync(
                    connection,
                    transaction,
                    batch,
                    streamKeyBinary,
                    staged,
                    current,
                    currentAuthority,
                    currentRawAcceptedThrough,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return;
            }

            ValidateCheckpointTransition(batch, current);

            var pending = await ReconcileObservationsAsync(
                connection,
                transaction,
                batch.Checkpoint,
                streamKeyBinary,
                staged,
                allowNewObservations: true,
                cancellationToken);

            if (current == batch.Checkpoint && pending.Length > 0)
            {
                throw new InvalidOperationException(
                    "A same-checkpoint acquisition cannot add raw observations.");
            }

            var positioned = AssignPositions(
                pending,
                currentRawAcceptedThrough);
            var resultingRawAcceptedThrough = positioned.Length == 0
                ? currentRawAcceptedThrough
                : positioned[^1].Position;

            var candidateAuthority = StageAuthority(
                streamId,
                batch.SuccessfulContactTime,
                resultingRawAcceptedThrough,
                currentAuthority);

            if (current != batch.Checkpoint)
            {
                await PersistCheckpointAsync(
                    connection,
                    transaction,
                    batch.Checkpoint,
                    streamKeyBinary,
                    current is null,
                    cancellationToken);
            }

            foreach (var observation in positioned)
            {
                await InsertObservationAsync(
                    connection,
                    transaction,
                    batch.Checkpoint,
                    streamKeyBinary,
                    observation,
                    cancellationToken);
            }

            if (currentAuthority != candidateAuthority)
            {
                await PersistAcquisitionContactAuthorityAsync(
                    connection,
                    transaction,
                    candidateAuthority,
                    streamKeyBinary,
                    currentAuthority is null,
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
        ObservationCheckpoint? current)
    {
        if (current is not null &&
            current.InstanceId == batch.Checkpoint.InstanceId &&
            batch.Checkpoint.NextSequence < current.NextSequence)
        {
            throw new InvalidOperationException(
                "A checkpoint cannot move backwards within an Agent instance.");
        }
    }

    private static async Task ValidateExactCurrentStateReplayAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationIngestionBatch batch,
        byte[] streamKeyBinary,
        IReadOnlyList<StagedObservation> staged,
        ObservationCheckpoint? current,
        AcquisitionContactAuthority? currentAuthority,
        ObservationPosition? currentRawAcceptedThrough,
        CancellationToken cancellationToken)
    {
        if (current is null || current != batch.Checkpoint)
        {
            throw new InvalidOperationException(
                "The durable checkpoint no longer matches the expected state.");
        }

        var pending = await ReconcileObservationsAsync(
            connection,
            transaction,
            batch.Checkpoint,
            streamKeyBinary,
            staged,
            allowNewObservations: false,
            cancellationToken);

        if (pending.Length != 0 ||
            currentAuthority is null ||
            !SameInstant(
                currentAuthority.SuccessfulContactTime,
                batch.SuccessfulContactTime) ||
            currentAuthority.RawAcceptedThrough != currentRawAcceptedThrough)
        {
            throw new InvalidOperationException(
                "The request is not an exact replay of the current committed acquisition state.");
        }
    }

    private static PositionedObservation[] AssignPositions(
        IReadOnlyList<StagedObservation> pending,
        ObservationPosition? currentRawAcceptedThrough)
    {
        if (pending.Count == 0)
        {
            return [];
        }

        var last = currentRawAcceptedThrough?.Value ?? 0UL;
        PositionedObservation[] positioned = new PositionedObservation[pending.Count];

        for (var index = 0; index < pending.Count; index++)
        {
            if (last == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The durable observation position space is exhausted.");
            }

            last++;
            positioned[index] = new PositionedObservation(
                pending[index],
                new ObservationPosition(last));
        }

        return positioned;
    }

    private static AcquisitionContactAuthority StageAuthority(
        ObservationStreamId streamId,
        DateTimeOffset successfulContactTime,
        ObservationPosition? rawAcceptedThrough,
        AcquisitionContactAuthority? current)
    {
        var normalizedContactTime = successfulContactTime.ToUniversalTime();

        if (current is not null &&
            SameInstant(current.SuccessfulContactTime, normalizedContactTime) &&
            current.RawAcceptedThrough == rawAcceptedThrough)
        {
            return current;
        }

        ulong revision;
        if (current is null)
        {
            revision = 0;
        }
        else
        {
            if (current.AcquisitionRevision.Value == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The acquisition authority revision space is exhausted.");
            }

            revision = current.AcquisitionRevision.Value + 1;
        }

        return new AcquisitionContactAuthority(
            streamId,
            normalizedContactTime,
            rawAcceptedThrough,
            new AcquisitionAuthorityRevision(revision));
    }

    private static bool SameInstant(
        DateTimeOffset left,
        DateTimeOffset right) =>
        left.UtcDateTime.Ticks == right.UtcDateTime.Ticks;

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

    private static async Task<AcquisitionContactAuthority?>
        ReadAcquisitionContactAuthorityAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            ObservationStreamId streamId,
            byte[] streamKeyBinary,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SuccessfulContactTime, RawAcceptedThrough, AcquisitionRevision
            FROM dbo.AcquisitionContactAuthority
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

        var rawAcceptedThrough = reader.IsDBNull(1)
            ? null
            : new ObservationPosition(
                SqlServerUInt64.Materialize(reader.GetDecimal(1)));

        return new AcquisitionContactAuthority(
            streamId,
            reader.GetDateTimeOffset(0),
            rawAcceptedThrough,
            new AcquisitionAuthorityRevision(
                SqlServerUInt64.Materialize(reader.GetDecimal(2))));
    }

    private static async Task<ObservationPosition?> ReadRawAcceptedThroughAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationStreamId streamId,
        byte[] streamKeyBinary,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(Position)
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;

        AddStreamIdentityParameters(command, streamId, streamKeyBinary);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : new ObservationPosition(
                SqlServerUInt64.Materialize((decimal)result));
    }

    private static async Task<StagedObservation[]> ReconcileObservationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationCheckpoint checkpoint,
        byte[] streamKeyBinary,
        IReadOnlyList<StagedObservation> staged,
        bool allowNewObservations,
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
                if (!allowNewObservations)
                {
                    throw new InvalidOperationException(
                        "An exact replay cannot add raw observations.");
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

        AddStreamIdentityParameters(command, checkpoint.StreamId, streamKeyBinary);
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@InstanceId", checkpoint.InstanceId));
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@Sequence", sequence));

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var type = (SignalType)reader.GetByte(2);
        var quality = (ObservationQuality)reader.GetByte(4);
        var persistedValue = reader.IsDBNull(3) ? null : reader.GetString(3);

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
            Value = SqlServerObservationValueCodec.Deserialize(type, persistedValue),
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

        AddStreamIdentityParameters(command, checkpoint.StreamId, streamKeyBinary);
        command.Parameters.Add(
            new SqlParameter("@StreamKey", SqlDbType.NVarChar, 256)
            {
                Value = checkpoint.StreamId.StreamKey,
            });
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@InstanceId", checkpoint.InstanceId));
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@NextSequence", checkpoint.NextSequence));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationCheckpoint checkpoint,
        byte[] streamKeyBinary,
        PositionedObservation positioned,
        CancellationToken cancellationToken)
    {
        var staged = positioned.Observation;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.MachineObservation
                (MachineId, StreamKeyBinary, InstanceId, Sequence, Position,
                 Source, Address, SignalType, ObservationValue,
                 Quality, ObservedAt)
            VALUES
                (@MachineId, @StreamKeyBinary, @InstanceId, @Sequence, @Position,
                 @Source, @Address, @SignalType, @ObservationValue,
                 @Quality, @ObservedAt);
            """;

        AddStreamIdentityParameters(command, checkpoint.StreamId, streamKeyBinary);
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@InstanceId", checkpoint.InstanceId));
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@Sequence", staged.Sequence));
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@Position", positioned.Position.Value));
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

    private static async Task PersistAcquisitionContactAuthorityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AcquisitionContactAuthority authority,
        byte[] streamKeyBinary,
        bool insert,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? """
                INSERT INTO dbo.AcquisitionContactAuthority
                    (MachineId, StreamKeyBinary, SuccessfulContactTime,
                     RawAcceptedThrough, AcquisitionRevision)
                VALUES
                    (@MachineId, @StreamKeyBinary, @SuccessfulContactTime,
                     @RawAcceptedThrough, @AcquisitionRevision);
                """
            : """
                UPDATE dbo.AcquisitionContactAuthority
                SET SuccessfulContactTime = @SuccessfulContactTime,
                    RawAcceptedThrough = @RawAcceptedThrough,
                    AcquisitionRevision = @AcquisitionRevision
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary;
                """;

        AddStreamIdentityParameters(
            command,
            authority.ObservationStreamId,
            streamKeyBinary);
        command.Parameters.Add(
            new SqlParameter("@SuccessfulContactTime", SqlDbType.DateTimeOffset)
            {
                Value = authority.SuccessfulContactTime.ToUniversalTime(),
            });
        command.Parameters.Add(
            new SqlParameter("@RawAcceptedThrough", SqlDbType.Decimal)
            {
                Precision = 20,
                Scale = 0,
                Value = authority.RawAcceptedThrough is null
                    ? DBNull.Value
                    : (decimal)authority.RawAcceptedThrough.Value,
            });
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@AcquisitionRevision",
                authority.AcquisitionRevision.Value));

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

    private sealed record PositionedObservation(
        StagedObservation Observation,
        ObservationPosition Position);
}
