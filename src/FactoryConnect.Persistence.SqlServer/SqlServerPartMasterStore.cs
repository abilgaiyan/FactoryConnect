using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed record PartMasterRecord(string PartNumber, string DisplayName,
    string? Description, decimal IdealCycleSeconds, bool IsActive);

public sealed class SqlServerPartMasterStore(string connectionString)
{
    public static void Validate(PartMasterRecord part, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (string.IsNullOrWhiteSpace(part.PartNumber) || part.PartNumber.Length > 200 ||
            part.PartNumber != part.PartNumber.Trim() ||
            string.IsNullOrWhiteSpace(part.DisplayName) || part.DisplayName.Length > 200 ||
            (part.Description?.Length ?? 0) > 1000 ||
            part.IdealCycleSeconds <= 0 || part.IdealCycleSeconds > 86400 ||
            decimal.Round(part.IdealCycleSeconds, 3) != part.IdealCycleSeconds ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid part master entry.");
    }

    public async Task UpsertAsync(PartMasterRecord part, string actor, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Validate(part, actor, now);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.PartMaster WITH (UPDLOCK, HOLDLOCK)
            SET DisplayName=@Name, Description=@Description, IdealCycleSeconds=@Cycle,
                IsActive=@Active, UpdatedBy=@Actor, UpdatedAtUtc=@Now
            WHERE PartNumber=@Number;
            IF @@ROWCOUNT = 0
                INSERT dbo.PartMaster (PartNumber, DisplayName, Description, IdealCycleSeconds, IsActive, UpdatedBy, UpdatedAtUtc)
                VALUES (@Number,@Name,@Description,@Cycle,@Active,@Actor,@Now);
            """;
        command.Parameters.Add("@Number", SqlDbType.NVarChar, 200).Value = part.PartNumber;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = part.DisplayName;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = (object?)part.Description ?? DBNull.Value;
        var cycle = command.Parameters.Add("@Cycle", SqlDbType.Decimal);
        cycle.Precision = 18;
        cycle.Scale = 3;
        cycle.Value = part.IdealCycleSeconds;
        command.Parameters.Add("@Active", SqlDbType.Bit).Value = part.IsActive;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 200).Value = actor;
        command.Parameters.Add("@Now", SqlDbType.DateTimeOffset).Value = now;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PartMasterRecord>> ListAsync(bool activeOnly, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PartNumber, DisplayName, Description, IdealCycleSeconds, IsActive
            FROM dbo.PartMaster WHERE (@ActiveOnly=0 OR IsActive=1)
            ORDER BY PartNumber OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """;
        command.Parameters.Add("@ActiveOnly", SqlDbType.Bit).Value = activeOnly;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = offset;
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;
        var rows = new List<PartMasterRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDecimal(3), reader.GetBoolean(4)));
        return rows;
    }
}
