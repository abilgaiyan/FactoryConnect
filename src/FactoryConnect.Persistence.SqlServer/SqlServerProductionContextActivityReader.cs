using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// SQL Server read-only adapter over durable FC-031 machine activity history.
/// </summary>
internal sealed class SqlServerProductionContextActivityReader :
    IProductionContextActivityReader
{
    private readonly string _connectionString;
    private readonly ObservationProcessorId _stateProcessorId;

    public SqlServerProductionContextActivityReader(
        string connectionString,
        ObservationProcessorId stateProcessorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(stateProcessorId);

        _connectionString = connectionString;
        _stateProcessorId = stateProcessorId;
    }

    public async Task<IReadOnlyList<DurableMachineActivityPeriod>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var streamKey = OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        var processorKey = StringOrderKeyV2Codec.Encode(_stateProcessorId.Value);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ValidateAuthorityIdentityAsync(
                connection,
                streamId,
                streamKey,
                processorKey,
                cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (@ReadLimit)
                       h.Position, h.ProjectionRevision, h.OutputOrdinal, h.MachineState,
                       h.StartedAt, h.EndedAt, h.InstanceId, h.Sequence
                FROM dbo.MachineActivityPeriodHistory AS h
                     WITH (INDEX(IX_MachineActivityPeriodHistory_StreamPosition))
                INNER JOIN dbo.MachineStateActivityAuthority AS a
                    ON a.MachineId = h.MachineId
                   AND a.StreamKeyBinary = h.StreamKeyBinary
                   AND a.StateProcessorIdOrderKey = h.StateProcessorIdOrderKey
                WHERE h.MachineId = @MachineId
                  AND h.StreamKeyBinary = @StreamKeyBinary
                  AND h.StateProcessorIdOrderKey = @StateProcessorIdOrderKey
                  AND a.StateProcessorId = @StateProcessorId
                  AND (@AfterPosition IS NULL OR h.Position > @AfterPosition)
                ORDER BY h.Position ASC, h.ProjectionRevision ASC, h.OutputOrdinal ASC;
                """;

            command.Parameters.Add("@ReadLimit", SqlDbType.Int).Value =
                batchSize == int.MaxValue ? int.MaxValue : batchSize + 1;
            AddIdentityParameters(command, streamId, streamKey, processorKey);

            var afterParameter = command.Parameters.Add("@AfterPosition", SqlDbType.Decimal);
            afterParameter.Precision = 20;
            afterParameter.Scale = 0;
            afterParameter.Value = afterPosition is null
                ? DBNull.Value
                : checked((decimal)afterPosition.Value);

            var result = new List<DurableMachineActivityPeriod>(batchSize);
            ObservationPosition? previous = afterPosition;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var position = new ObservationPosition(
                    SqlServerUInt64.Materialize(reader.GetDecimal(0)));
                if (previous is not null && position <= previous)
                {
                    throw Corruption(
                        "Persisted machine activity history positions are not strictly increasing.");
                }

                previous = position;
                _ = SqlServerUInt64.Materialize(reader.GetDecimal(1));
                if (reader.GetInt32(2) < 0)
                {
                    throw Corruption("Persisted machine activity output ordinal is invalid.");
                }

                var state = CheckedMachineState(reader.GetByte(3));
                var startedAt = reader.GetDateTimeOffset(4);
                var endedAt = reader.GetDateTimeOffset(5);
                if (endedAt < startedAt)
                {
                    throw Corruption("Persisted machine activity interval is invalid.");
                }

                if (result.Count == batchSize)
                {
                    break;
                }

                result.Add(new DurableMachineActivityPeriod(
                    _stateProcessorId,
                    position,
                    streamId,
                    SqlServerUInt64.Materialize(reader.GetDecimal(6)),
                    SqlServerUInt64.Materialize(reader.GetDecimal(7)),
                    new MachineActivityPeriod(
                        streamId.MachineId,
                        state,
                        startedAt,
                        endedAt)));
            }

            return result;
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Machine activity-history read was cancelled.",
                exception,
                cancellationToken);
        }
    }

    private async Task ValidateAuthorityIdentityAsync(
        SqlConnection connection,
        ObservationStreamId streamId,
        byte[] streamKey,
        byte[] processorKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StateProcessorId, StateProcessorIdOrderKey
            FROM dbo.MachineStateActivityAuthority
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND (StateProcessorIdOrderKey = @StateProcessorIdOrderKey
                   OR StateProcessorId = @StateProcessorId)
            ORDER BY StateProcessorIdOrderKey;
            """;
        AddIdentityParameters(command, streamId, streamKey, processorKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        ValidatePersistedProcessorIdentity(reader, processorKey);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Corruption(
                "Persisted machine state/activity authority has conflicting rows for one semantic identity.");
        }
    }

    private void ValidatePersistedProcessorIdentity(
        SqlDataReader reader,
        byte[] expectedProcessorKey)
    {
        var persistedProcessorId = reader.GetString(0);
        var persistedProcessorKey = (byte[])reader[1];

        if (string.IsNullOrWhiteSpace(persistedProcessorId) ||
            !string.Equals(
                persistedProcessorId,
                _stateProcessorId.Value,
                StringComparison.Ordinal) ||
            !persistedProcessorKey.AsSpan().SequenceEqual(expectedProcessorKey) ||
            !persistedProcessorKey.AsSpan().SequenceEqual(
                StringOrderKeyV2Codec.Encode(persistedProcessorId)))
        {
            throw Corruption(
                "Persisted machine state/activity processor identity is inconsistent.");
        }
    }

    private void AddIdentityParameters(
        SqlCommand command,
        ObservationStreamId streamId,
        byte[] streamKey,
        byte[] processorKey)
    {
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            streamId.MachineId.Value;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            OrdinalStringKeyCodec.MaxCodeUnits * 2).Value = streamKey;
        command.Parameters.Add(
            "@StateProcessorIdOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value = processorKey;
        command.Parameters.Add("@StateProcessorId", SqlDbType.NVarChar, 256).Value =
            _stateProcessorId.Value;
    }

    private static MachineState CheckedMachineState(byte value)
    {
        var state = (MachineState)value;
        if (!Enum.IsDefined(state))
        {
            throw Corruption("Persisted machine activity state is invalid.");
        }

        return state;
    }

    private static InvalidOperationException Corruption(string message) => new(message);
}
