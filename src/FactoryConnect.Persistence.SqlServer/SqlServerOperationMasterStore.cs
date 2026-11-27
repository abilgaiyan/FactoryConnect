using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed record OperationMasterRecord(string PartNumber, string OperationCode,
    string DisplayName, decimal? IdealCycleSeconds, bool IsActive);

public sealed class SqlServerOperationMasterStore(string connectionString)
{
    private static bool ValidKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && value == value.Trim();

    public static void Validate(OperationMasterRecord operation, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!ValidKey(operation.PartNumber) || !ValidKey(operation.OperationCode) ||
            string.IsNullOrWhiteSpace(operation.DisplayName) || operation.DisplayName.Length > 200 ||
            (operation.IdealCycleSeconds is decimal cycle &&
                (cycle <= 0 || cycle > 86400 || decimal.Round(cycle, 3) != cycle)) ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid operation master entry.");
    }

    public async Task UpsertAsync(OperationMasterRecord operation, string actor, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Validate(operation, actor, now);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.OperationMaster WITH (UPDLOCK, HOLDLOCK)
            SET DisplayName=@Name, IdealCycleSeconds=@Cycle, IsActive=@Active,
                UpdatedBy=@Actor, UpdatedAtUtc=@Now
            WHERE PartNumber=@Part AND OperationCode=@Code;
            IF @@ROWCOUNT = 0
                INSERT dbo.OperationMaster (PartNumber, OperationCode, DisplayName, IdealCycleSeconds, IsActive, UpdatedBy, UpdatedAtUtc)
                VALUES (@Part,@Code,@Name,@Cycle,@Active,@Actor,@Now);
            """;
        command.Parameters.Add("@Part", SqlDbType.NVarChar, 200).Value = operation.PartNumber;
        command.Parameters.Add("@Code", SqlDbType.NVarChar, 200).Value = operation.OperationCode;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = operation.DisplayName;
        var cycle = command.Parameters.Add("@Cycle", SqlDbType.Decimal);
        cycle.Precision = 18;
        cycle.Scale = 3;
        cycle.Value = (object?)operation.IdealCycleSeconds ?? DBNull.Value;
        command.Parameters.Add("@Active", SqlDbType.Bit).Value = operation.IsActive;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 200).Value = actor;
        command.Parameters.Add("@Now", SqlDbType.DateTimeOffset).Value = now;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationMasterRecord>> ListAsync(string partNumber, bool activeOnly,
        int offset, int limit, CancellationToken cancellationToken = default)
    {
        if (!ValidKey(partNumber) || offset < 0 || limit is < 1 or > 100)
            throw new ArgumentException("Invalid operation master page.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.PartNumber, o.OperationCode, o.DisplayName, o.IdealCycleSeconds, o.IsActive
            FROM dbo.OperationMaster o JOIN dbo.PartMaster p ON p.PartNumber=o.PartNumber
            WHERE o.PartNumber=@Part AND (@ActiveOnly=0 OR (o.IsActive=1 AND p.IsActive=1))
            ORDER BY o.OperationCode OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """;
        command.Parameters.Add("@Part", SqlDbType.NVarChar, 200).Value = partNumber;
        command.Parameters.Add("@ActiveOnly", SqlDbType.Bit).Value = activeOnly;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = offset;
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;
        var rows = new List<OperationMasterRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3), reader.GetBoolean(4)));
        return rows;
    }
}
