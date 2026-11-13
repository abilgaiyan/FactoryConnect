using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Records one immutable local-commissioning review decision per exact declaration.
/// This does not qualify or publish production quantity.
/// </summary>
public sealed class SqlServerSupervisorProductionEntryReviewStore
{
    private readonly string _connectionString;

    public SqlServerSupervisorProductionEntryReviewStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<bool> DecideAsync(Guid machineId, Guid entryId, Guid decisionId, string decision,
        string reason, string reviewer, DateTimeOffset reviewedAtUtc, CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || entryId == Guid.Empty || decisionId == Guid.Empty ||
            decision is not ("Approved" or "Rejected") ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 2000 ||
            string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 200 ||
            reviewedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid local review decision.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntry WITH (HOLDLOCK)
                           WHERE EntryId = @EntryId AND MachineId = @MachineId)
                THROW 51026, 'Supervisor declaration not found.', 1;
            IF EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntry
                       WHERE EntryId = @EntryId AND EnteredBy = @Reviewer)
                THROW 51027, 'A declaration cannot be reviewed by its author.', 1;

            IF EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntryReview WITH (UPDLOCK, HOLDLOCK)
                       WHERE EntryId = @EntryId OR DecisionId = @DecisionId)
            BEGIN
                IF NOT EXISTS
                (
                    SELECT 1 FROM dbo.SupervisorProductionEntryReview
                    WHERE EntryId = @EntryId AND DecisionId = @DecisionId
                      AND Decision = @Decision AND Reason = @Reason AND ReviewedBy = @Reviewer
                )
                    THROW 51028, 'Review already decided or decision ID reused.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                INSERT INTO dbo.SupervisorProductionEntryReview
                    (EntryId, DecisionId, Decision, Reason, ReviewedBy, ReviewedAtUtc)
                VALUES (@EntryId, @DecisionId, @Decision, @Reason, @Reviewer, @ReviewedAt);
                SELECT CAST(1 AS bit);
            END
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = entryId;
        command.Parameters.Add("@DecisionId", SqlDbType.UniqueIdentifier).Value = decisionId;
        command.Parameters.Add("@Decision", SqlDbType.NVarChar, 20).Value = decision;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 2000).Value = reason;
        command.Parameters.Add("@Reviewer", SqlDbType.NVarChar, 200).Value = reviewer;
        command.Parameters.Add("@ReviewedAt", SqlDbType.DateTimeOffset).Value = reviewedAtUtc;
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Review decision returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<SupervisorProductionEntryReviewRecord>> ListAsync(
        Guid machineId, DateTimeOffset shiftStartsAtUtc, DateTimeOffset shiftEndsAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || shiftStartsAtUtc.Offset != TimeSpan.Zero ||
            shiftEndsAtUtc.Offset != TimeSpan.Zero || shiftEndsAtUtc <= shiftStartsAtUtc ||
            shiftEndsAtUtc - shiftStartsAtUtc > TimeSpan.FromHours(24))
            throw new ArgumentException("Exact machine and UTC shift are required.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (100) e.EntryId, r.DecisionId, r.Decision, r.Reason,
                   r.ReviewedBy, r.ReviewedAtUtc
            FROM dbo.SupervisorProductionEntry AS e
            LEFT JOIN dbo.SupervisorProductionEntryReview AS r ON r.EntryId = e.EntryId
            WHERE e.MachineId = @MachineId
              AND e.ShiftStartsAtUtc = @Starts AND e.ShiftEndsAtUtc = @Ends
            ORDER BY e.RecordedAtUtc DESC, e.EntryId DESC;
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = shiftStartsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = shiftEndsAtUtc;
        var rows = new List<SupervisorProductionEntryReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new SupervisorProductionEntryReviewRecord(reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? "Pending" : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        return rows;
    }
}

public sealed record SupervisorProductionEntryReviewRecord(Guid EntryId, Guid? DecisionId,
    string Decision, string? Reason, string? ReviewedBy, DateTimeOffset? ReviewedAtUtc);
