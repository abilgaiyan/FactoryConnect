using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Immutable decision for one downtime explanation; never updates activity or OEE.</summary>
public sealed record SupervisorDowntimeExplanationReviewRecord(
    Guid ExplanationId, Guid? DecisionId, string Decision, string? Reason,
    string? ReviewedBy, DateTimeOffset? ReviewedAtUtc);

public sealed class SqlServerSupervisorDowntimeExplanationReviewStore
{
    private readonly string _connectionString;
    public SqlServerSupervisorDowntimeExplanationReviewStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<bool> DecideAsync(Guid machineId, Guid explanationId, Guid decisionId,
        string decision, string reason, string reviewer, DateTimeOffset reviewedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || explanationId == Guid.Empty || decisionId == Guid.Empty ||
            decision is not ("Approved" or "Rejected") ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 2000 ||
            string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 200 ||
            reviewedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid downtime explanation review.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @Author nvarchar(200);
            SELECT @Author = EnteredBy FROM dbo.SupervisorDowntimeExplanation WITH (UPDLOCK, HOLDLOCK)
            WHERE ExplanationId = @ExplanationId AND MachineId = @MachineId;
            IF @Author IS NULL
                THROW 51041, 'Downtime explanation not found for machine.', 1;
            IF @Author = @Reviewer
                THROW 51042, 'Downtime explanation author cannot review own entry.', 1;

            IF EXISTS (SELECT 1 FROM dbo.SupervisorDowntimeExplanationReview WITH (UPDLOCK, HOLDLOCK)
                       WHERE ExplanationId = @ExplanationId OR DecisionId = @DecisionId)
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.SupervisorDowntimeExplanationReview
                    WHERE ExplanationId = @ExplanationId AND DecisionId = @DecisionId
                      AND Decision = @Decision AND Reason = @Reason AND ReviewedBy = @Reviewer)
                    THROW 51043, 'Conflicting downtime explanation review.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                INSERT INTO dbo.SupervisorDowntimeExplanationReview
                    (ExplanationId, DecisionId, Decision, Reason, ReviewedBy, ReviewedAtUtc)
                VALUES (@ExplanationId, @DecisionId, @Decision, @Reason, @Reviewer, @At);
                SELECT CAST(1 AS bit);
            END
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@ExplanationId", SqlDbType.UniqueIdentifier).Value = explanationId;
        command.Parameters.Add("@DecisionId", SqlDbType.UniqueIdentifier).Value = decisionId;
        command.Parameters.Add("@Decision", SqlDbType.NVarChar, 20).Value = decision;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 2000).Value = reason;
        command.Parameters.Add("@Reviewer", SqlDbType.NVarChar, 200).Value = reviewer;
        command.Parameters.Add("@At", SqlDbType.DateTimeOffset).Value = reviewedAtUtc;
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Downtime explanation review returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<SupervisorDowntimeExplanationReviewRecord>> ListAsync(
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
            SELECT TOP (500) e.ExplanationId, r.DecisionId, r.Decision, r.Reason,
                   r.ReviewedBy, r.ReviewedAtUtc
            FROM dbo.SupervisorDowntimeExplanation AS e
            LEFT JOIN dbo.SupervisorDowntimeExplanationReview AS r ON r.ExplanationId = e.ExplanationId
            WHERE e.MachineId = @Machine AND e.ActivityStartsAtUtc < @To AND e.ActivityEndsAtUtc > @From
            ORDER BY e.ActivityStartsAtUtc, e.ExplanationId;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@From", SqlDbType.DateTimeOffset).Value = fromUtc;
        command.Parameters.Add("@To", SqlDbType.DateTimeOffset).Value = toUtc;
        var result = new List<SupervisorDowntimeExplanationReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? "Pending" : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        return result;
    }
}
