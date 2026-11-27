using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed class SqlServerSupervisorCycleReviewStore(string connectionString)
{
    public async Task<bool> AppendAsync(SupervisorCycleReview review, CancellationToken cancellationToken = default)
    {
        Validate(review);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.SupervisorShiftIdealCycle WITH (UPDLOCK, HOLDLOCK)
                WHERE CycleRevisionId = @Cycle AND RecordedAtUtc <= @At)
                THROW 51144, 'Cycle revision missing or review precedes declaration.', 1;
            IF EXISTS (SELECT 1 FROM dbo.SupervisorCycleReview WITH (UPDLOCK, HOLDLOCK)
                WHERE ReviewId = @Review OR CycleRevisionId = @Cycle)
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.SupervisorCycleReview
                    WHERE ReviewId = @Review AND CycleRevisionId = @Cycle AND IsAccepted = @Accepted
                    AND ReviewedBy = @Actor AND DATALENGTH(ReviewedBy) = DATALENGTH(@Actor))
                    THROW 51145, 'Conflicting terminal cycle review.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                INSERT dbo.SupervisorCycleReview (ReviewId, CycleRevisionId, IsAccepted, ReviewedBy, ReviewedAtUtc)
                VALUES (@Review, @Cycle, @Accepted, @Actor, @At);
                SELECT CAST(1 AS bit);
            END
            """;
        command.Parameters.Add("@Review", SqlDbType.UniqueIdentifier).Value = review.ReviewId;
        command.Parameters.Add("@Cycle", SqlDbType.UniqueIdentifier).Value = review.CycleRevisionId;
        command.Parameters.Add("@Accepted", SqlDbType.Bit).Value = review.Decision == SupervisorCycleReviewDecision.Accepted;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 200).Value = review.ReviewedBy;
        command.Parameters.Add("@At", SqlDbType.DateTimeOffset).Value = review.ReviewedAtUtc;
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Review append returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<SupervisorCycleReview>> ListAsync(Guid machineId, string assignmentId,
        string shiftId, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
        DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || string.IsNullOrWhiteSpace(assignmentId) || assignmentId.Length > 200 ||
            string.IsNullOrWhiteSpace(shiftId) || shiftId.Length > 200 || startsAtUtc.Offset != TimeSpan.Zero ||
            endsAtUtc.Offset != TimeSpan.Zero || asOfUtc.Offset != TimeSpan.Zero ||
            endsAtUtc <= startsAtUtc || endsAtUtc - startsAtUtc > TimeSpan.FromDays(1))
            throw new ArgumentException("Invalid review scope or cut.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.ReviewId, r.CycleRevisionId, r.IsAccepted, r.ReviewedBy, r.ReviewedAtUtc
            FROM dbo.SupervisorCycleReview r
            INNER JOIN dbo.SupervisorShiftIdealCycle c ON c.CycleRevisionId = r.CycleRevisionId
            WHERE c.MachineId = @Machine AND c.ShiftScheduleAssignmentId = @Assignment
                AND DATALENGTH(c.ShiftScheduleAssignmentId) = DATALENGTH(@Assignment)
                AND c.ShiftId = @Shift AND DATALENGTH(c.ShiftId) = DATALENGTH(@Shift)
                AND c.ShiftStartsAtUtc = @Starts AND c.ShiftEndsAtUtc = @Ends
                AND r.ReviewedAtUtc <= @Cut
            ORDER BY c.RevisionNumber, r.ReviewId;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@Assignment", SqlDbType.NVarChar, 200).Value = assignmentId;
        command.Parameters.Add("@Shift", SqlDbType.NVarChar, 200).Value = shiftId;
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = startsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = endsAtUtc;
        command.Parameters.Add("@Cut", SqlDbType.DateTimeOffset).Value = asOfUtc;
        var result = new List<SupervisorCycleReview>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetGuid(1),
                reader.GetBoolean(2) ? SupervisorCycleReviewDecision.Accepted : SupervisorCycleReviewDecision.Rejected,
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    private static void Validate(SupervisorCycleReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.ReviewId == Guid.Empty || review.CycleRevisionId == Guid.Empty ||
            !Enum.IsDefined(review.Decision) || string.IsNullOrWhiteSpace(review.ReviewedBy) ||
            review.ReviewedBy.Length > 200 || review.ReviewedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid cycle review.");
    }
}
