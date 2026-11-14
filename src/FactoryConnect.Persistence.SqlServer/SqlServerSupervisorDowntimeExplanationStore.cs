using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Local-only append store. Callers must first verify the period against the
/// authoritative activity reader and enforce commissioning authorization.
/// This store never modifies machine activity or OEE.
/// </summary>
public sealed record SupervisorDowntimeExplanationRecord(
    Guid ExplanationId, Guid MachineId, string StateProcessorId, long ObservationPosition,
    DateTimeOffset ActivityStartsAtUtc, DateTimeOffset ActivityEndsAtUtc,
    string ReasonCode, string Explanation, string EnteredBy, DateTimeOffset EnteredAtUtc);

public sealed class SqlServerSupervisorDowntimeExplanationStore
{
    private readonly string _connectionString;

    public SqlServerSupervisorDowntimeExplanationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<bool> AppendAsync(SupervisorDowntimeExplanationRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ExplanationId == Guid.Empty || record.MachineId == Guid.Empty ||
            record.ObservationPosition <= 0 ||
            record.ActivityStartsAtUtc.Offset != TimeSpan.Zero ||
            record.ActivityEndsAtUtc.Offset != TimeSpan.Zero ||
            record.EnteredAtUtc.Offset != TimeSpan.Zero ||
            record.ActivityStartsAtUtc >= record.ActivityEndsAtUtc)
            throw new ArgumentException("Invalid downtime explanation identity or UTC interval.");
        Check(record.StateProcessorId, 200);
        Check(record.ReasonCode, 100);
        Check(record.Explanation, 2000);
        Check(record.EnteredBy, 200);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF EXISTS (SELECT 1 FROM dbo.SupervisorDowntimeExplanation WITH (UPDLOCK, HOLDLOCK)
                       WHERE ExplanationId = @Id
                          OR (MachineId = @Machine AND StateProcessorId = @Processor AND ObservationPosition = @Position))
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.SupervisorDowntimeExplanation
                    WHERE ExplanationId = @Id AND MachineId = @Machine
                      AND StateProcessorId = @Processor AND ObservationPosition = @Position
                      AND ActivityStartsAtUtc = @Starts AND ActivityEndsAtUtc = @Ends
                      AND ReasonCode = @Reason AND Explanation = @Explanation AND EnteredBy = @Actor)
                    THROW 51040, 'Downtime explanation already exists or ID reused.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                INSERT INTO dbo.SupervisorDowntimeExplanation
                    (ExplanationId, MachineId, StateProcessorId, ObservationPosition,
                     ActivityStartsAtUtc, ActivityEndsAtUtc, ReasonCode, Explanation, EnteredBy, EnteredAtUtc)
                VALUES (@Id, @Machine, @Processor, @Position,
                        @Starts, @Ends, @Reason, @Explanation, @Actor, @At);
                SELECT CAST(1 AS bit);
            END
            """;
        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = record.ExplanationId;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = record.MachineId;
        command.Parameters.Add("@Processor", SqlDbType.NVarChar, 200).Value = record.StateProcessorId;
        command.Parameters.Add("@Position", SqlDbType.BigInt).Value = record.ObservationPosition;
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = record.ActivityStartsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = record.ActivityEndsAtUtc;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 100).Value = record.ReasonCode;
        command.Parameters.Add("@Explanation", SqlDbType.NVarChar, 2000).Value = record.Explanation;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 200).Value = record.EnteredBy;
        command.Parameters.Add("@At", SqlDbType.DateTimeOffset).Value = record.EnteredAtUtc;
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Downtime explanation append returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<SupervisorDowntimeExplanationRecord>> ListAsync(
        Guid machineId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || fromUtc.Offset != TimeSpan.Zero ||
            toUtc.Offset != TimeSpan.Zero || fromUtc >= toUtc ||
            toUtc - fromUtc > TimeSpan.FromDays(31))
            throw new ArgumentException("Invalid machine or UTC interval.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (500) ExplanationId, MachineId, StateProcessorId, ObservationPosition,
                   ActivityStartsAtUtc, ActivityEndsAtUtc, ReasonCode, Explanation, EnteredBy, EnteredAtUtc
            FROM dbo.SupervisorDowntimeExplanation
            WHERE MachineId = @Machine AND ActivityStartsAtUtc < @To AND ActivityEndsAtUtc > @From
            ORDER BY ActivityStartsAtUtc, ExplanationId;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@From", SqlDbType.DateTimeOffset).Value = fromUtc;
        command.Parameters.Add("@To", SqlDbType.DateTimeOffset).Value = toUtc;
        var result = new List<SupervisorDowntimeExplanationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        return result;
    }

    private static void Check(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum)
            throw new ArgumentException("Required downtime explanation text missing or too long.");
    }
}
