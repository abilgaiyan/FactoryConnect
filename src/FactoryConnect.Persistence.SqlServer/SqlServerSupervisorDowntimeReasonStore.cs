using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed record SupervisorDowntimeReasonRecord(
    string ReasonCode, string DisplayName, string? Description, bool IsActive);

/// <summary>Catalog persistence for local supervisor downtime reason administration.</summary>
public sealed class SqlServerSupervisorDowntimeReasonStore
{
    private readonly string _connectionString;

    public SqlServerSupervisorDowntimeReasonStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task UpsertAsync(SupervisorDowntimeReasonRecord reason,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (string.IsNullOrWhiteSpace(reason.ReasonCode) || reason.ReasonCode.Length > 100 ||
            reason.ReasonCode != reason.ReasonCode.Trim() ||
            string.IsNullOrWhiteSpace(reason.DisplayName) || reason.DisplayName.Length > 200 ||
            (reason.Description?.Length ?? 0) > 1000 ||
            nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid downtime reason catalog entry.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.SupervisorDowntimeReason WITH (UPDLOCK, HOLDLOCK)
            SET DisplayName = @Name, Description = @Description, IsActive = @Active,
                UpdatedAtUtc = @Now
            WHERE ReasonCode = @Code;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.SupervisorDowntimeReason
                    (ReasonCode, DisplayName, Description, IsActive, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Code, @Name, @Description, @Active, @Now, @Now);
            """;
        command.Parameters.Add("@Code", SqlDbType.NVarChar, 100).Value = reason.ReasonCode;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = reason.DisplayName;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
            (object?)reason.Description ?? DBNull.Value;
        command.Parameters.Add("@Active", SqlDbType.Bit).Value = reason.IsActive;
        command.Parameters.Add("@Now", SqlDbType.DateTimeOffset).Value = nowUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
