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
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (@BatchSize)
                       Position, ProjectionRevision, OutputOrdinal, MachineState,
                       StartedAt, EndedAt, InstanceId, Sequence
                FROM dbo.MachineActivityPeriodHistory
                     WITH (INDEX(IX_MachineActivityPeriodHistory_StreamPosition))
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary
                  AND StateProcessorIdOrderKey = @StateProcessorIdOrderKey
                  AND (@AfterPosition IS NULL OR Position > @AfterPosition)
                ORDER BY Position ASC, ProjectionRevision ASC, OutputOrdinal ASC;
                """;

            command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
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

            var afterParameter = command.Parameters.Add(
                "@AfterPosition",
                SqlDbType.Decimal);
            afterParameter.Precision = 20;
            afterParameter.Scale = 0;
            afterParameter.Value = afterPosition is null
                ? DBNull.Value
                : checked((decimal)afterPosition.Value);

            var result = new List<DurableMachineActivityPeriod>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var position = new ObservationPosition(
                    SqlServerUInt64.Materialize(reader.GetDecimal(0)));
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

                var instanceId = SqlServerUInt64.Materialize(reader.GetDecimal(6));
                var sequence = SqlServerUInt64.Materialize(reader.GetDecimal(7));
                result.Add(new DurableMachineActivityPeriod(
                    _stateProcessorId,
                    position,
                    streamId,
                    instanceId,
                    sequence,
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
