using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;
using System.Data;

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

        var instanceId = MaterializeUInt64(reader.GetDecimal(0));
        var nextSequence = MaterializeUInt64(reader.GetDecimal(1));

        return new ObservationCheckpoint(
            streamId,
            instanceId,
            nextSequence);
    }

    public ValueTask CommitAsync(
        ObservationIngestionBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException(
            "SQL Server ingestion commits are implemented in FC-023.5.");
    }

    private static ulong MaterializeUInt64(decimal value) =>
        checked((ulong)value);
}
