using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed record CommissioningProcessor(
    long RowId, Guid MachineId, string ProcessorId, string StreamKey,
    string? Position, bool AdmissionBound, DateTimeOffset? ProductionStartsAt);

/// <summary>Read-only heads, never calls the policy-binding checkpoint store.</summary>
public static class CommissioningProcessorReader
{
    public static async Task<CommissioningProcessor[]> ReadAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        var settings = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = 3,
            ApplicationName = "FactoryConnect.Commissioning.ReadOnly",
        };
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 3;
        command.CommandText = """
            SELECT TOP (129) p.ProductionContextProcessorRowId, p.MachineId,
                p.ProcessorKey, p.ObservationStreamKey, c.Position,
                p.AdmissionBound, p.ProductionStartsAt
            FROM dbo.ProductionContextProcessor AS p
            LEFT JOIN dbo.ProductionContextCheckpoint AS c
                ON c.ProductionContextProcessorRowId = p.ProductionContextProcessorRowId
            ORDER BY p.ProductionContextProcessorRowId;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<CommissioningProcessor> rows = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == 128)
                throw new InvalidOperationException("Commissioning head limit exceeded.");
            rows.Add(new CommissioningProcessor(reader.GetInt64(0), reader.GetGuid(1),
                reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4).ToString(CultureInfo.InvariantCulture),
                reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6)));
        }
        return rows.ToArray();
    }
}
