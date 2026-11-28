using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Immutable ideal cycle declaration revision. This is not production quantity or OEE evidence.
/// </summary>
public sealed record SupervisorShiftIdealCycleRevision(
    Guid CycleRevisionId, Guid MachineId, string ShiftScheduleAssignmentId,
    string ShiftId, DateTimeOffset ShiftStartsAtUtc, DateTimeOffset ShiftEndsAtUtc,
    int RevisionNumber, decimal IdealCycleSeconds,
    string? ProductionOrder, string? PartNumber, string? Operation,
    string? RevisionReason, string EnteredBy, DateTimeOffset RecordedAtUtc);

/// <summary>SQL-backed exact-occurrence planning revisions with serialized head checks.</summary>
public sealed class SqlServerSupervisorShiftIdealCycleStore
{
    private readonly string _connectionString;

    public SqlServerSupervisorShiftIdealCycleStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <returns>True if appended; false for an identical retry of the same revision ID.</returns>
    /// <exception cref="InvalidOperationException">Unknown roster occurrence, stale head, or conflicting retry.</exception>
    public async Task<bool> AppendAsync(SupervisorShiftIdealCycleRevision revision,
        Guid? expectedHeadRevisionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        Validate(revision);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            -- The machine and exact shift occurrence must belong to the commissioned roster.
            IF NOT EXISTS (
                SELECT 1
                FROM dbo.MachineShiftOccurrenceRosterOccurrence AS o WITH (HOLDLOCK)
                INNER JOIN dbo.MachineShiftOccurrenceRoster AS r WITH (HOLDLOCK)
                    ON r.MachineShiftOccurrenceRosterRowId = o.MachineShiftOccurrenceRosterRowId
                WHERE r.MachineId = @Machine
                  AND o.ShiftScheduleAssignmentId = @Assignment
                  AND o.ShiftId = @Shift
                  AND o.ShiftStartsAtUtc = @Starts
                  AND o.ShiftEndsAtUtc = @Ends)
                THROW 51140, 'Exact machine shift occurrence not present in roster.', 1;

            -- A stable application lock serializes even the first revision (empty key range).
            DECLARE @LockResult int;
            EXEC @LockResult = sp_getapplock
                @Resource = @LockKey, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000;
            IF @LockResult < 0 THROW 51141, 'Shift ideal cycle revision lock unavailable.', 1;

            IF EXISTS (SELECT 1 FROM dbo.SupervisorShiftIdealCycle WHERE CycleRevisionId = @RevisionId)
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM dbo.SupervisorShiftIdealCycle
                    WHERE CycleRevisionId = @RevisionId AND MachineId = @Machine
                      AND ShiftScheduleAssignmentId = @Assignment AND ShiftId = @Shift
                      AND ShiftStartsAtUtc = @Starts AND ShiftEndsAtUtc = @Ends
                      AND RevisionNumber = @Number AND IdealCycleSeconds = @Quantity
                      AND EnteredBy = @Actor
                      AND ((ProductionOrder = @Order) OR (ProductionOrder IS NULL AND @Order IS NULL))
                      AND ((PartNumber = @Part) OR (PartNumber IS NULL AND @Part IS NULL))
                      AND ((Operation = @Operation) OR (Operation IS NULL AND @Operation IS NULL))
                      AND ((RevisionReason = @Reason) OR (RevisionReason IS NULL AND @Reason IS NULL)))
                    THROW 51142, 'Revision ID reused with different cycle contents.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                DECLARE @Head uniqueidentifier, @HeadNumber int;
                SELECT TOP (1) @Head = CycleRevisionId, @HeadNumber = RevisionNumber
                FROM dbo.SupervisorShiftIdealCycle WITH (UPDLOCK, HOLDLOCK)
                WHERE MachineId = @Machine AND ShiftScheduleAssignmentId = @Assignment
                  AND ShiftStartsAtUtc = @Starts AND ShiftEndsAtUtc = @Ends
                  AND ((ProductionOrder = @Order) OR (ProductionOrder IS NULL AND @Order IS NULL))
                  AND ((PartNumber = @Part) OR (PartNumber IS NULL AND @Part IS NULL))
                  AND ((Operation = @Operation) OR (Operation IS NULL AND @Operation IS NULL))
                ORDER BY RevisionNumber DESC;

                IF (@Head IS NULL AND @Expected IS NOT NULL)
                    OR (@Head IS NOT NULL AND (@Expected IS NULL OR @Head <> @Expected))
                    OR @Number <> ISNULL(@HeadNumber, 0) + 1
                    THROW 51143, 'Stale shift ideal cycle revision head.', 1;

                -- A correction changes the cycle value, not the production population.
                IF @Head IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM dbo.SupervisorShiftIdealCycle
                    WHERE CycleRevisionId = @Head AND ShiftId = @Shift
                      AND ((ProductionOrder = @Order) OR (ProductionOrder IS NULL AND @Order IS NULL))
                      AND ((PartNumber = @Part) OR (PartNumber IS NULL AND @Part IS NULL))
                      AND ((Operation = @Operation) OR (Operation IS NULL AND @Operation IS NULL)))
                    THROW 51143, 'Cycle correction cannot change recorded production context.', 1;

                INSERT INTO dbo.SupervisorShiftIdealCycle
                    (CycleRevisionId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                     ShiftStartsAtUtc, ShiftEndsAtUtc, RevisionNumber, IdealCycleSeconds,
                     ProductionOrder, PartNumber, Operation, RevisionReason,
                     EnteredBy, RecordedAtUtc)
                VALUES (@RevisionId, @Machine, @Assignment, @Shift, @Starts, @Ends,
                        @Number, @Quantity, @Order, @Part, @Operation,
                        @Reason, @Actor, @Recorded);
                SELECT CAST(1 AS bit);
            END
            """;
        AddParameters(command, revision);
        command.Parameters.Add("@Expected", SqlDbType.UniqueIdentifier).Value =
            (object?)expectedHeadRevisionId ?? DBNull.Value;
        // SQL application lock resources have a 255-character limit; hash the exact
        // binary identity to avoid truncation collisions.
        var identity = $"{revision.MachineId:N}|{revision.ShiftScheduleAssignmentId}|{revision.ShiftStartsAtUtc:O}|{revision.ShiftEndsAtUtc:O}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        command.Parameters.Add("@LockKey", SqlDbType.NVarChar, 255).Value =
            "SupervisorShiftIdealCycle:" + Convert.ToHexString(hash);
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Cycle append returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<SupervisorShiftIdealCycleRevision>> ListAsync(
        Guid machineId, string assignmentId, DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || string.IsNullOrWhiteSpace(assignmentId) ||
            assignmentId.Length > 200 || startsAtUtc.Offset != TimeSpan.Zero ||
            endsAtUtc.Offset != TimeSpan.Zero || endsAtUtc <= startsAtUtc)
            throw new ArgumentException("Invalid exact shift occurrence.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ListCoreAsync(connection, null, machineId, assignmentId, startsAtUtc, endsAtUtc, cancellationToken);
    }

    internal static async Task<IReadOnlyList<SupervisorShiftIdealCycleRevision>> ListCoreAsync(SqlConnection connection, SqlTransaction? transaction,
        Guid machineId, string assignmentId, DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CycleRevisionId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                   ShiftStartsAtUtc, ShiftEndsAtUtc, RevisionNumber, IdealCycleSeconds,
                   ProductionOrder, PartNumber, Operation, RevisionReason,
                   EnteredBy, RecordedAtUtc
            FROM dbo.SupervisorShiftIdealCycle
            WHERE MachineId = @Machine AND ShiftScheduleAssignmentId = @Assignment
              AND ShiftStartsAtUtc = @Starts AND ShiftEndsAtUtc = @Ends
            ORDER BY ProductionOrder, PartNumber, Operation, RevisionNumber ASC;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        AddText(command, "@Assignment", assignmentId, 200);
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = startsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = endsAtUtc;
        var result = new List<SupervisorShiftIdealCycleRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SupervisorShiftIdealCycleRevision(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt32(6),
                reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12), reader.GetFieldValue<DateTimeOffset>(13)));
        return result;
    }

    private static void Validate(SupervisorShiftIdealCycleRevision r)
    {
        if (r.CycleRevisionId == Guid.Empty || r.MachineId == Guid.Empty ||
            r.RevisionNumber < 1 || r.IdealCycleSeconds <= 0 ||
            decimal.Round(r.IdealCycleSeconds, 3) != r.IdealCycleSeconds ||
            r.IdealCycleSeconds > 86400m ||
            r.ShiftStartsAtUtc.Offset != TimeSpan.Zero ||
            r.ShiftEndsAtUtc.Offset != TimeSpan.Zero ||
            r.RecordedAtUtc.Offset != TimeSpan.Zero ||
            r.ShiftEndsAtUtc <= r.ShiftStartsAtUtc ||
            r.ShiftEndsAtUtc - r.ShiftStartsAtUtc > TimeSpan.FromDays(1))
            throw new ArgumentException("Invalid cycle revision identity, quantity or UTC interval.");
        Required(r.ShiftScheduleAssignmentId, 200);
        Required(r.ShiftId, 200);
        Required(r.EnteredBy, 200);
        Optional(r.ProductionOrder, 200);
        Optional(r.PartNumber, 200);
        Optional(r.Operation, 200);
        Optional(r.RevisionReason, 2000);
        if (r.RevisionNumber > 1) Required(r.RevisionReason, 2000);
    }

    private static void Required(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new ArgumentException("Required cycle field missing or too long.");
    }

    private static void Optional(string? value, int max)
    {
        if (value?.Length > max) throw new ArgumentException("Target field too long.");
    }

    private static void AddText(SqlCommand command, string name, string? value, int max) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, max).Value =
            (object?)value ?? DBNull.Value;

    private static void AddParameters(SqlCommand command, SupervisorShiftIdealCycleRevision r)
    {
        command.Parameters.Add("@RevisionId", SqlDbType.UniqueIdentifier).Value = r.CycleRevisionId;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = r.MachineId;
        AddText(command, "@Assignment", r.ShiftScheduleAssignmentId, 200);
        AddText(command, "@Shift", r.ShiftId, 200);
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = r.ShiftStartsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = r.ShiftEndsAtUtc;
        command.Parameters.Add("@Number", SqlDbType.Int).Value = r.RevisionNumber;
        var quantity = command.Parameters.Add("@Quantity", SqlDbType.Decimal);
        quantity.Precision = 18;
        quantity.Scale = 3;
        quantity.Value = r.IdealCycleSeconds;
        AddText(command, "@Order", r.ProductionOrder, 200);
        AddText(command, "@Part", r.PartNumber, 200);
        AddText(command, "@Operation", r.Operation, 200);
        AddText(command, "@Reason", r.RevisionReason, 2000);
        AddText(command, "@Actor", r.EnteredBy, 200);
        command.Parameters.Add("@Recorded", SqlDbType.DateTimeOffset).Value = r.RecordedAtUtc;
    }
}
