using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricProjectionSourceBindingReader
{
    private readonly string _connectionString;

    public SqlServerOperationalMetricProjectionSourceBindingReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask<MachineId?> ReadMachineIdAsync(
        OperationalMetricProjectionProcessorId processorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var projectionProcessorKeyBinary = StringOrderKeyV2Codec.Encode(processorId.Value);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pp.ProcessorKey, pp.ProcessorKeyBinary, pp.MetricInputStreamRowId, " +
            "ap.ProcessorKey, ap.ProcessorKeyBinary, ap.MetricInputStreamRowId, " +
            "s.MachineId, s.StreamKey, s.StreamKeyBinary " +
            "FROM dbo.OperationalMetricProjectionProcessor AS pp " +
            "INNER JOIN dbo.MetricAggregationProcessor AS ap " +
            "ON ap.MetricAggregationProcessorRowId = pp.MetricAggregationProcessorRowId " +
            "INNER JOIN dbo.MetricInputStream AS s " +
            "ON s.MetricInputStreamRowId = pp.MetricInputStreamRowId " +
            "WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add(
            "@ProcessorKeyBinary",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value = projectionProcessorKeyBinary;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var persistedProjectionProcessorKey = reader.GetString(0);
        var persistedProjectionProcessorKeyBinary = (byte[])reader[1];
        if (!string.Equals(persistedProjectionProcessorKey, processorId.Value, StringComparison.Ordinal) ||
            !persistedProjectionProcessorKeyBinary.AsSpan().SequenceEqual(projectionProcessorKeyBinary))
        {
            throw Corrupt("projection processor identity");
        }

        var projectionStreamRowId = reader.GetInt64(2);
        var aggregationProcessorKey = reader.GetString(3);
        var aggregationProcessorKeyBinary = (byte[])reader[4];
        var aggregationStreamRowId = reader.GetInt64(5);
        var machineId = new MachineId(reader.GetGuid(6));
        var streamKey = reader.GetString(7);
        var streamKeyBinary = (byte[])reader[8];

        if (aggregationStreamRowId != projectionStreamRowId ||
            !aggregationProcessorKeyBinary.AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(aggregationProcessorKey)) ||
            !streamKeyBinary.AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(streamKey)))
        {
            throw Corrupt("projection processor source binding");
        }

        return machineId;
    }

    private static InvalidOperationException Corrupt(string field) =>
        new($"Persisted operational metric projection {field} is corrupt or unsupported.");
}
