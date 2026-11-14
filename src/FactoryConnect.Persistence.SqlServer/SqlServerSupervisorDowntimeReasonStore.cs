using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed record SupervisorDowntimeReasonRecord(
    string ReasonCode, string DisplayName, string? Description, bool IsActive);

/// <summary>Read-only catalog for local supervisor downtime reason selection.</summary>
public sealed class SqlServerSupervisorDowntimeReasonStore
{
    private readonly string _connectionString;

    public SqlServerSupervisorDowntimeReasonStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<SupervisorDowntimeReasonRecord>> ListAsync(
        bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        var reasons = new List<SupervisorDowntimeReasonRecord>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReasonCode, DisplayName, Description, IsActive
            FROM dbo.SupervisorDowntimeReason
            WHERE (@ActiveOnly = 0 OR IsActive = 1)
            ORDER BY DisplayName, ReasonCode;
            """;
        command.Parameters.Add("@ActiveOnly", SqlDbType.Bit).Value = activeOnly;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            reasons.Add(new SupervisorDowntimeReasonRecord(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3)));
        return reasons;
    }
}
