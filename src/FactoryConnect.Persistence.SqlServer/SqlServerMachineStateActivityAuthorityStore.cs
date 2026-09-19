using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlServerMachineStateActivityPublicationStage
{
    AuthorityLocked,
    AuthorityMutated,
    SignalsDeleted,
    SignalsInserted,
    StateHistoryInserted,
    ActivityHistoryInserted,
}

/// <summary>
/// SQL Server implementation of the FC-031 joint state/activity authority.
/// Projection, current signals, derived output batches, and evaluation authority
/// are published in one serializable transaction.
/// </summary>
internal sealed class SqlServerMachineStateActivityAuthorityStore :
    IMachineStateActivityAuthorityStore
{
    private readonly string _connectionString;
    private readonly Func<SqlServerMachineStateActivityPublicationStage, int, CancellationToken, Task>? _testHook;

    internal SqlServerMachineStateActivityAuthorityStore(string connectionString)
        : this(connectionString, null)
    {
    }

    internal SqlServerMachineStateActivityAuthorityStore(
        string connectionString,
        Func<SqlServerMachineStateActivityPublicationStage, int, CancellationToken, Task>? testHook)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _testHook = testHook;
    }

    public async ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        cancellationToken.ThrowIfCancellationRequested();

        var streamKey = OrdinalStringKeyCodec.Encode(observationStreamId.StreamKey);
        var processorKey = StringOrderKeyV2Codec.Encode(stateProcessorId.Value);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadCurrentAsync(
                connection, null, stateProcessorId, observationStreamId,
                streamKey, processorKey, false, cancellationToken).ConfigureAwait(false);
            return current?.Snapshot;
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Machine state/activity authority read was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(
        MachineStateActivityAuthorityPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();

        var processorId = publication.Projection.ProcessorId;
        var streamId = publication.Projection.StreamId;
        var streamKey = OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        var processorKey = StringOrderKeyV2Codec.Encode(processorId.Value);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);

            try
            {
                var current = await ReadCurrentAsync(
                    connection, transaction, processorId, streamId,
                    streamKey, processorKey, true, cancellationToken).ConfigureAwait(false);
                var sessionId = _testHook is null
                    ? 0
                    : await ReadSessionIdAsync(
                        connection, transaction, cancellationToken).ConfigureAwait(false);
                await VisitAsync(
                    SqlServerMachineStateActivityPublicationStage.AuthorityLocked,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

                if (current is not null &&
                    await IsExactReplayAsync(
                        connection, transaction, current, publication,
                        streamKey, processorKey, cancellationToken).ConfigureAwait(false))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new MachineStateActivityAuthorityPublicationAccepted(
                        current.Snapshot,
                        EvaluationAuthorityPublicationDisposition.ExactReplay);
                }

                if (!MatchesExpected(current, publication) ||
                    !IsValidAdvancement(current, publication))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new MachineStateActivityAuthorityPublicationConflict(current?.Snapshot);
                }

                if (current is not null &&
                    current.Snapshot.EvaluationAuthority.ProjectionRevision.Value == ulong.MaxValue)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new MachineStateActivityAuthorityRevisionExhausted(current.Snapshot);
                }

                var revision = current is null
                    ? 0UL
                    : current.Snapshot.EvaluationAuthority.ProjectionRevision.Value + 1UL;

                if (current is null)
                {
                    await InsertAuthorityAsync(
                        connection, transaction, publication, revision,
                        streamKey, processorKey, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await UpdateAuthorityAsync(
                        connection, transaction, publication, revision,
                        streamKey, processorKey,
                        current.Snapshot.EvaluationAuthority.ProjectionRevision.Value,
                        cancellationToken).ConfigureAwait(false);
                }

                await VisitAsync(
                    SqlServerMachineStateActivityPublicationStage.AuthorityMutated,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

                await ReplaceSignalsAsync(
                    connection, transaction, publication.Projection,
                    streamKey, processorKey,
                    (stage, token) => VisitAsync(stage, sessionId, token),
                    cancellationToken).ConfigureAwait(false);
                await InsertStateChangesAsync(
                    connection, transaction, publication.StateChanges, revision,
                    streamKey, processorKey, cancellationToken).ConfigureAwait(false);
                await VisitAsync(
                    SqlServerMachineStateActivityPublicationStage.StateHistoryInserted,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);
                await InsertActivityPeriodsAsync(
                    connection, transaction, publication.ActivityPeriods, revision,
                    streamKey, processorKey, cancellationToken).ConfigureAwait(false);
                await VisitAsync(
                    SqlServerMachineStateActivityPublicationStage.ActivityHistoryInserted,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                var authority = CreateAuthority(publication.EvaluationIdentity, revision);
                return new MachineStateActivityAuthorityPublicationAccepted(
                    new MachineStateActivityAuthoritySnapshot(publication.Projection, authority),
                    EvaluationAuthorityPublicationDisposition.NewPublication);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Machine state/activity authority publication was cancelled.",
                exception,
                cancellationToken);
        }
    }

    private static async Task<CurrentPublication?> ReadCurrentAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        byte[] streamKey,
        byte[] processorKey,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate
            ? " WITH (UPDLOCK, HOLDLOCK, INDEX(PK_MachineStateActivityAuthority))"
            : string.Empty;
        command.CommandText =
            "SELECT StateProcessorId, StateProcessorIdOrderKey, Position, MachineState, " +
            "ActiveState, ActiveStartedAt, LastConsumedInstanceId, ContinuityPolicyIdentity, " +
            "ContinuityPolicyVersion, ProjectionRevision " +
            "FROM dbo.MachineStateActivityAuthority" + hint + " " +
            "WHERE MachineId = @MachineId AND StreamKeyBinary = @StreamKeyBinary AND " +
            "(StateProcessorIdOrderKey = @StateProcessorIdOrderKey OR StateProcessorId = @StateProcessorId) " +
            "ORDER BY StateProcessorIdOrderKey;";

        AddIdentityParameters(command, processorId, streamId, streamKey, processorKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var persistedProcessor = reader.GetString(0);
        var persistedKey = (byte[])reader[1];
        if (!string.Equals(persistedProcessor, processorId.Value, StringComparison.Ordinal) ||
            !persistedKey.AsSpan().SequenceEqual(processorKey) ||
            !persistedKey.AsSpan().SequenceEqual(StringOrderKeyV2Codec.Encode(persistedProcessor)))
        {
            throw Corruption("Persisted machine state/activity processor identity is inconsistent.");
        }

        var position = new ObservationPosition(SqlServerUInt64.Materialize(reader.GetDecimal(2)));
        var state = CheckedEnum<MachineState>(reader.GetByte(3), "machine state");
        var activeState = reader.IsDBNull(4)
            ? (MachineState?)null
            : CheckedEnum<MachineState>(reader.GetByte(4), "active state");
        var activeStartedAt = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(5);
        if (activeState.HasValue != activeStartedAt.HasValue)
        {
            throw Corruption("Persisted machine state/activity active-state pair is inconsistent.");
        }

        var lastInstance = SqlServerUInt64.Materialize(reader.GetDecimal(6));
        var policy = new CurrentStatePolicyReference(reader.GetString(7), reader.GetString(8));
        var revision = SqlServerUInt64.Materialize(reader.GetDecimal(9));

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Corruption("Persisted machine state/activity authority has conflicting semantic rows.");
        }

        await reader.DisposeAsync().ConfigureAwait(false);

        var signals = await ReadSignalsAsync(
            connection, transaction, streamId, streamKey, processorKey, cancellationToken)
            .ConfigureAwait(false);
        var projection = new MachineStateActivityProjection(
            processorId, streamId, position, signals, state, activeState, activeStartedAt);
        var authority = new EvaluationAuthority(
            processorId, streamId, position, state, lastInstance, policy,
            new StateProjectionAuthorityRevision(revision));
        return new CurrentPublication(
            new MachineStateActivityAuthoritySnapshot(projection, authority),
            revision);
    }

    private static async Task<MachineSignalValue[]> ReadSignalsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        ObservationStreamId streamId,
        byte[] streamKey,
        byte[] processorKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SignalOrdinal, SignalKey, SignalType, DigitalValue, DecimalValue,
                   CounterValue, WholeNumberValue, TextValue, TimestampValue,
                   Source, Quality, Timestamp
            FROM dbo.MachineStateActivitySignal
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND StateProcessorIdOrderKey = @StateProcessorIdOrderKey
            ORDER BY SignalOrdinal;
            """;
        AddChildIdentityParameters(command, streamId, streamKey, processorKey);

        var signals = new List<MachineSignalValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var expectedOrdinal = 0;
        string? previousKey = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != expectedOrdinal++)
            {
                throw Corruption("Persisted machine signal ordinals are not contiguous.");
            }

            var key = reader.GetString(1);
            if (previousKey is not null &&
                StringComparer.OrdinalIgnoreCase.Compare(previousKey, key) >= 0)
            {
                throw Corruption("Persisted machine signals are not uniquely ordered by key.");
            }
            previousKey = key;

            var type = CheckedEnum<SignalType>(reader.GetByte(2), "signal type");
            var quality = CheckedEnum<ObservationQuality>(reader.GetByte(10), "observation quality");
            signals.Add(new MachineSignalValue
            {
                Key = key,
                Type = type,
                Value = MaterializeSignalValue(reader, type),
                Source = reader.IsDBNull(9) ? null : reader.GetString(9),
                Quality = quality,
                Timestamp = reader.GetDateTimeOffset(11),
            });
        }

        return [.. signals];
    }

    private static object? MaterializeSignalValue(SqlDataReader reader, SignalType type)
    {
        var populated = 0;
        for (var i = 3; i <= 8; i++)
        {
            if (!reader.IsDBNull(i)) populated++;
        }

        if (populated == 0) return null;
        if (populated != 1) throw Corruption("Persisted machine signal has an invalid value shape.");

        try
        {
            return type switch
            {
                SignalType.Digital when !reader.IsDBNull(3) => reader.GetBoolean(3),
                SignalType.Analog or SignalType.Numeric when !reader.IsDBNull(4) =>
                    decimal.Parse(reader.GetString(4), NumberStyles.Float, CultureInfo.InvariantCulture),
                SignalType.Counter when !reader.IsDBNull(5) => SqlServerUInt64.Materialize(reader.GetDecimal(5)),
                SignalType.WholeNumber when !reader.IsDBNull(6) => reader.GetInt64(6),
                SignalType.Text or SignalType.Enumeration when !reader.IsDBNull(7) => reader.GetString(7),
                SignalType.Timestamp when !reader.IsDBNull(8) => reader.GetDateTimeOffset(8),
                _ => throw Corruption("Persisted machine signal value does not match its signal type."),
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw Corruption("Persisted machine signal value is invalid.", exception);
        }
    }

    private static async Task<bool> IsExactReplayAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentPublication current,
        MachineStateActivityAuthorityPublication publication,
        byte[] streamKey,
        byte[] processorKey,
        CancellationToken cancellationToken)
    {
        var snapshot = current.Snapshot;
        var identity = publication.EvaluationIdentity;
        if (!ProjectionEquals(snapshot.Projection, publication.Projection) ||
            snapshot.EvaluationAuthority.StateProcessorId != identity.StateProcessorId ||
            snapshot.EvaluationAuthority.ObservationStreamId != identity.ObservationStreamId ||
            snapshot.EvaluationAuthority.EvaluatedThrough != identity.EvaluatedThrough ||
            snapshot.EvaluationAuthority.MachineState != identity.MachineState ||
            snapshot.EvaluationAuthority.LastConsumedInstanceId != identity.LastConsumedInstanceId ||
            snapshot.EvaluationAuthority.AppliedContinuityPolicy != identity.AppliedContinuityPolicy)
        {
            return false;
        }

        var stateChanges = await ReadStateChangesAsync(
            connection, transaction, publication.Projection, current.Revision,
            streamKey, processorKey, cancellationToken).ConfigureAwait(false);
        if (!stateChanges.SequenceEqual(publication.StateChanges)) return false;

        var periods = await ReadActivityPeriodsAsync(
            connection, transaction, publication.Projection, current.Revision,
            streamKey, processorKey, cancellationToken).ConfigureAwait(false);
        return periods.SequenceEqual(publication.ActivityPeriods);
    }

    private static async Task<DurableMachineStateChangedEvent[]> ReadStateChangesAsync(
        SqlConnection connection, SqlTransaction transaction,
        MachineStateActivityProjection projection, ulong revision,
        byte[] streamKey, byte[] processorKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT OutputOrdinal, Position, PreviousState, CurrentState, OccurredAt, InstanceId, Sequence
            FROM dbo.MachineStateChangeHistory
            WHERE MachineId = @MachineId AND StreamKeyBinary = @StreamKeyBinary
              AND StateProcessorIdOrderKey = @StateProcessorIdOrderKey
              AND ProjectionRevision = @ProjectionRevision
            ORDER BY OutputOrdinal;
            """;
        AddChildIdentityParameters(command, projection.StreamId, streamKey, processorKey);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ProjectionRevision", revision));
        var result = new List<DurableMachineStateChangedEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != ordinal++) throw Corruption("Persisted state-change output ordinals are not contiguous.");
            var position = new ObservationPosition(SqlServerUInt64.Materialize(reader.GetDecimal(1)));
            var previous = CheckedEnum<MachineState>(reader.GetByte(2), "previous machine state");
            var current = CheckedEnum<MachineState>(reader.GetByte(3), "current machine state");
            result.Add(new DurableMachineStateChangedEvent(
                projection.ProcessorId, position, projection.StreamId,
                SqlServerUInt64.Materialize(reader.GetDecimal(5)),
                SqlServerUInt64.Materialize(reader.GetDecimal(6)),
                new MachineStateChangedEvent(projection.StreamId.MachineId, previous, current, reader.GetDateTimeOffset(4))));
        }
        return [.. result];
    }

    private static async Task<DurableMachineActivityPeriod[]> ReadActivityPeriodsAsync(
        SqlConnection connection, SqlTransaction transaction,
        MachineStateActivityProjection projection, ulong revision,
        byte[] streamKey, byte[] processorKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT OutputOrdinal, Position, MachineState, StartedAt, EndedAt, InstanceId, Sequence
            FROM dbo.MachineActivityPeriodHistory
            WHERE MachineId = @MachineId AND StreamKeyBinary = @StreamKeyBinary
              AND StateProcessorIdOrderKey = @StateProcessorIdOrderKey
              AND ProjectionRevision = @ProjectionRevision
            ORDER BY OutputOrdinal;
            """;
        AddChildIdentityParameters(command, projection.StreamId, streamKey, processorKey);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ProjectionRevision", revision));
        var result = new List<DurableMachineActivityPeriod>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != ordinal++) throw Corruption("Persisted activity-period output ordinals are not contiguous.");
            var started = reader.GetDateTimeOffset(3);
            var ended = reader.GetDateTimeOffset(4);
            if (ended < started) throw Corruption("Persisted activity period has an invalid interval.");
            result.Add(new DurableMachineActivityPeriod(
                projection.ProcessorId,
                new ObservationPosition(SqlServerUInt64.Materialize(reader.GetDecimal(1))),
                projection.StreamId,
                SqlServerUInt64.Materialize(reader.GetDecimal(5)),
                SqlServerUInt64.Materialize(reader.GetDecimal(6)),
                new MachineActivityPeriod(
                    projection.StreamId.MachineId,
                    CheckedEnum<MachineState>(reader.GetByte(2), "activity machine state"),
                    started, ended)));
        }
        return [.. result];
    }

    private static async Task InsertAuthorityAsync(
        SqlConnection connection, SqlTransaction transaction,
        MachineStateActivityAuthorityPublication publication, ulong revision,
        byte[] streamKey, byte[] processorKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT dbo.MachineStateActivityAuthority
                (MachineId, StreamKeyBinary, StateProcessorId, StateProcessorIdOrderKey,
                 Position, MachineState, ActiveState, ActiveStartedAt, LastConsumedInstanceId,
                 ContinuityPolicyIdentity, ContinuityPolicyVersion, ProjectionRevision)
            VALUES
                (@MachineId, @StreamKeyBinary, @StateProcessorId, @StateProcessorIdOrderKey,
                 @Position, @MachineState, @ActiveState, @ActiveStartedAt, @LastConsumedInstanceId,
                 @PolicyIdentity, @PolicyVersion, @ProjectionRevision);
            """;
        AddPublicationParameters(command, publication, revision, streamKey, processorKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Initial machine state/activity authority publication failed.");
    }

    private static async Task UpdateAuthorityAsync(
        SqlConnection connection, SqlTransaction transaction,
        MachineStateActivityAuthorityPublication publication, ulong revision,
        byte[] streamKey, byte[] processorKey, ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.MachineStateActivityAuthority
            SET Position=@Position, MachineState=@MachineState, ActiveState=@ActiveState,
                ActiveStartedAt=@ActiveStartedAt, LastConsumedInstanceId=@LastConsumedInstanceId,
                ContinuityPolicyIdentity=@PolicyIdentity, ContinuityPolicyVersion=@PolicyVersion,
                ProjectionRevision=@ProjectionRevision
            WHERE MachineId=@MachineId AND StreamKeyBinary=@StreamKeyBinary
              AND StateProcessorIdOrderKey=@StateProcessorIdOrderKey
              AND ProjectionRevision=@ExpectedRevision;
            """;
        AddPublicationParameters(command, publication, revision, streamKey, processorKey);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ExpectedRevision", expectedRevision));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Machine state/activity authority revision conflict.");
    }

    private static void AddPublicationParameters(
        SqlCommand command, MachineStateActivityAuthorityPublication publication,
        ulong revision, byte[] streamKey, byte[] processorKey)
    {
        var projection = publication.Projection;
        AddIdentityParameters(command, projection.ProcessorId, projection.StreamId, streamKey, processorKey);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", projection.Position.Value));
        command.Parameters.Add("@MachineState", SqlDbType.TinyInt).Value = (byte)projection.State;
        command.Parameters.Add("@ActiveState", SqlDbType.TinyInt).Value =
            projection.ActiveState is null ? DBNull.Value : (byte)projection.ActiveState.Value;
        command.Parameters.Add("@ActiveStartedAt", SqlDbType.DateTimeOffset).Value =
            projection.ActiveStartedAt is null ? DBNull.Value : projection.ActiveStartedAt.Value;
        command.Parameters.Add(SqlServerUInt64.CreateParameter(
            "@LastConsumedInstanceId", publication.EvaluationIdentity.LastConsumedInstanceId));
        command.Parameters.Add("@PolicyIdentity", SqlDbType.NVarChar, 256).Value =
            publication.EvaluationIdentity.AppliedContinuityPolicy.Identity;
        command.Parameters.Add("@PolicyVersion", SqlDbType.NVarChar, 256).Value =
            publication.EvaluationIdentity.AppliedContinuityPolicy.Version;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ProjectionRevision", revision));
    }

    private static async Task ReplaceSignalsAsync(
        SqlConnection connection, SqlTransaction transaction,
        MachineStateActivityProjection projection,
        byte[] streamKey, byte[] processorKey,
        Func<SqlServerMachineStateActivityPublicationStage, CancellationToken, Task> visitAsync,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE dbo.MachineStateActivitySignal
                WHERE MachineId=@MachineId AND StreamKeyBinary=@StreamKeyBinary
                  AND StateProcessorIdOrderKey=@StateProcessorIdOrderKey;
                """;
            AddChildIdentityParameters(delete, projection.StreamId, streamKey, processorKey);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await visitAsync(
            SqlServerMachineStateActivityPublicationStage.SignalsDeleted,
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < projection.Signals.Count; i++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT dbo.MachineStateActivitySignal
                    (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, SignalOrdinal,
                     SignalKey, SignalType, DigitalValue, DecimalValue, CounterValue,
                     WholeNumberValue, TextValue, TimestampValue, Source, Quality, Timestamp)
                VALUES
                    (@MachineId, @StreamKeyBinary, @StateProcessorIdOrderKey, @Ordinal,
                     @SignalKey, @SignalType, @DigitalValue, @DecimalValue, @CounterValue,
                     @WholeNumberValue, @TextValue, @TimestampValue, @Source, @Quality, @Timestamp);
                """;
            AddChildIdentityParameters(command, projection.StreamId, streamKey, processorKey);
            var signal = projection.Signals[i];
            command.Parameters.Add("@Ordinal", SqlDbType.Int).Value = i;
            command.Parameters.Add("@SignalKey", SqlDbType.NVarChar, -1).Value = signal.Key;
            command.Parameters.Add("@SignalType", SqlDbType.TinyInt).Value = (byte)signal.Type;
            AddSignalValueParameters(command, signal);
            command.Parameters.Add("@Source", SqlDbType.NVarChar, -1).Value =
                signal.Source is null ? DBNull.Value : signal.Source;
            command.Parameters.Add("@Quality", SqlDbType.TinyInt).Value = (byte)signal.Quality;
            command.Parameters.Add("@Timestamp", SqlDbType.DateTimeOffset).Value = signal.Timestamp;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await visitAsync(
            SqlServerMachineStateActivityPublicationStage.SignalsInserted,
            cancellationToken).ConfigureAwait(false);
    }

    private Task VisitAsync(
        SqlServerMachineStateActivityPublicationStage stage,
        int sessionId,
        CancellationToken cancellationToken) =>
        _testHook?.Invoke(stage, sessionId, cancellationToken) ?? Task.CompletedTask;

    private static async Task<int> ReadSessionIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static void AddSignalValueParameters(SqlCommand command, MachineSignalValue signal)
    {
        object digital = DBNull.Value, dec = DBNull.Value, counter = DBNull.Value,
            whole = DBNull.Value, text = DBNull.Value, timestamp = DBNull.Value;
        if (signal.Value is not null)
        {
            switch (signal.Type)
            {
                case SignalType.Digital: digital = (bool)signal.Value; break;
                case SignalType.Analog:
                case SignalType.Numeric: dec = CanonicalDecimalTextV1Codec.Serialize((decimal)signal.Value); break;
                case SignalType.Counter: counter = checked((decimal)(ulong)signal.Value); break;
                case SignalType.WholeNumber: whole = (long)signal.Value; break;
                case SignalType.Text:
                case SignalType.Enumeration: text = (string)signal.Value; break;
                case SignalType.Timestamp: timestamp = (DateTimeOffset)signal.Value; break;
                default: throw new ArgumentOutOfRangeException(nameof(signal), signal.Type, "Unsupported machine signal type.");
            }
        }
        command.Parameters.Add("@DigitalValue", SqlDbType.Bit).Value = digital;
        command.Parameters.Add("@DecimalValue", SqlDbType.NVarChar, 64).Value = dec;
        var cp = command.Parameters.Add("@CounterValue", SqlDbType.Decimal); cp.Precision = 20; cp.Scale = 0; cp.Value = counter;
        command.Parameters.Add("@WholeNumberValue", SqlDbType.BigInt).Value = whole;
        command.Parameters.Add("@TextValue", SqlDbType.NVarChar, -1).Value = text;
        command.Parameters.Add("@TimestampValue", SqlDbType.DateTimeOffset).Value = timestamp;
    }

    private static async Task InsertStateChangesAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<DurableMachineStateChangedEvent> items, ulong revision,
        byte[] streamKey, byte[] processorKey, CancellationToken cancellationToken)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT dbo.MachineStateChangeHistory
                    (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision,
                     OutputOrdinal, Position, PreviousState, CurrentState, OccurredAt, InstanceId, Sequence)
                VALUES
                    (@MachineId, @StreamKeyBinary, @StateProcessorIdOrderKey, @Revision,
                     @Ordinal, @Position, @PreviousState, @CurrentState, @OccurredAt, @InstanceId, @Sequence);
                """;
            AddChildIdentityParameters(command, item.StreamId, streamKey, processorKey);
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", revision));
            command.Parameters.Add("@Ordinal", SqlDbType.Int).Value = i;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", item.Position.Value));
            command.Parameters.Add("@PreviousState", SqlDbType.TinyInt).Value = (byte)item.StateChanged.PreviousState;
            command.Parameters.Add("@CurrentState", SqlDbType.TinyInt).Value = (byte)item.StateChanged.CurrentState;
            command.Parameters.Add("@OccurredAt", SqlDbType.DateTimeOffset).Value = item.StateChanged.Timestamp;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@InstanceId", item.InstanceId));
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Sequence", item.Sequence));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertActivityPeriodsAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<DurableMachineActivityPeriod> items, ulong revision,
        byte[] streamKey, byte[] processorKey, CancellationToken cancellationToken)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Period.EndedAt < item.Period.StartedAt)
                throw new ArgumentException("Activity period end may not precede its start.", nameof(items));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT dbo.MachineActivityPeriodHistory
                    (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision,
                     OutputOrdinal, Position, MachineState, StartedAt, EndedAt, InstanceId, Sequence)
                VALUES
                    (@MachineId, @StreamKeyBinary, @StateProcessorIdOrderKey, @Revision,
                     @Ordinal, @Position, @MachineState, @StartedAt, @EndedAt, @InstanceId, @Sequence);
                """;
            AddChildIdentityParameters(command, item.StreamId, streamKey, processorKey);
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", revision));
            command.Parameters.Add("@Ordinal", SqlDbType.Int).Value = i;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", item.Position.Value));
            command.Parameters.Add("@MachineState", SqlDbType.TinyInt).Value = (byte)item.Period.State;
            command.Parameters.Add("@StartedAt", SqlDbType.DateTimeOffset).Value = item.Period.StartedAt;
            command.Parameters.Add("@EndedAt", SqlDbType.DateTimeOffset).Value = item.Period.EndedAt;
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@InstanceId", item.InstanceId));
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Sequence", item.Sequence));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool MatchesExpected(CurrentPublication? current, MachineStateActivityAuthorityPublication publication) =>
        current is null
            ? publication.ExpectedProjectionPosition is null && publication.ExpectedAuthorityRevision is null
            : publication.ExpectedProjectionPosition == current.Snapshot.Projection.Position &&
              publication.ExpectedAuthorityRevision == current.Snapshot.EvaluationAuthority.ProjectionRevision;

    private static bool IsValidAdvancement(CurrentPublication? current, MachineStateActivityAuthorityPublication publication)
    {
        if (current is null) return true;
        var position = current.Snapshot.Projection.Position;
        return publication.Projection.Position > position &&
               publication.StateChanges.All(item => item.Position > position) &&
               publication.ActivityPeriods.All(item => item.Position > position);
    }

    private static bool ProjectionEquals(MachineStateActivityProjection left, MachineStateActivityProjection right) =>
        left.ProcessorId == right.ProcessorId &&
        left.StreamId == right.StreamId &&
        left.Position == right.Position &&
        left.State == right.State &&
        left.ActiveState == right.ActiveState &&
        left.ActiveStartedAt == right.ActiveStartedAt &&
        left.Signals.SequenceEqual(right.Signals);

    private static EvaluationAuthority CreateAuthority(EvaluationAuthorityReplayIdentity identity, ulong revision) =>
        new(identity.StateProcessorId, identity.ObservationStreamId, identity.EvaluatedThrough,
            identity.MachineState, identity.LastConsumedInstanceId, identity.AppliedContinuityPolicy,
            new StateProjectionAuthorityRevision(revision));

    private static void AddIdentityParameters(
        SqlCommand command, ObservationProcessorId processorId, ObservationStreamId streamId,
        byte[] streamKey, byte[] processorKey)
    {
        AddChildIdentityParameters(command, streamId, streamKey, processorKey);
        command.Parameters.Add("@StateProcessorId", SqlDbType.NVarChar, 256).Value = processorId.Value;
    }

    private static void AddChildIdentityParameters(
        SqlCommand command, ObservationStreamId streamId, byte[] streamKey, byte[] processorKey)
    {
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = streamId.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, OrdinalStringKeyCodec.MaxCodeUnits * 2).Value = streamKey;
        command.Parameters.Add("@StateProcessorIdOrderKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = processorKey;
    }

    private static T CheckedEnum<T>(byte value, string field) where T : struct, Enum
    {
        var result = (T)Enum.ToObject(typeof(T), value);
        if (!Enum.IsDefined(result)) throw Corruption($"Persisted {field} is invalid.");
        return result;
    }

    private static InvalidOperationException Corruption(string message, Exception? inner = null) =>
        new(message, inner);

    private sealed record CurrentPublication(
        MachineStateActivityAuthoritySnapshot Snapshot,
        ulong Revision);
}
