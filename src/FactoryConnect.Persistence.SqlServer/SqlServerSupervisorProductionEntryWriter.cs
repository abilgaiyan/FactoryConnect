using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Appends a supervisor declaration only for an existing exact machine-shift roster occurrence.
/// The declaration is never treated as CNC evidence or a published production quantity.
/// </summary>
public sealed class SqlServerSupervisorProductionEntryWriter
{
    private readonly string _connectionString;

    public SqlServerSupervisorProductionEntryWriter(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<bool> AppendAsync(SupervisorProductionEntryRecord entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.EntryId == Guid.Empty || entry.MachineId == Guid.Empty ||
            entry.ShiftStartsAtUtc.Offset != TimeSpan.Zero || entry.ShiftEndsAtUtc.Offset != TimeSpan.Zero ||
            entry.RecordedAtUtc.Offset != TimeSpan.Zero ||
            entry.ShiftEndsAtUtc <= entry.ShiftStartsAtUtc ||
            entry.ShiftEndsAtUtc - entry.ShiftStartsAtUtc > TimeSpan.FromHours(24))
            throw new ArgumentException("Valid identity and ordered UTC shift are required.", nameof(entry));
        Check(entry.ShiftScheduleAssignmentId, 200);
        Check(entry.ShiftId, 200);
        Check(entry.ProductionOrder, 200);
        Check(entry.PartNumber, 200);
        Check(entry.Operation, 200);
        Check(entry.EnteredBy, 200);
        if (entry.Note?.Length > 2000) throw new ArgumentException("Note exceeds 2000 characters.", nameof(entry));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM dbo.MachineShiftOccurrenceRosterOccurrence AS o WITH (HOLDLOCK)
                INNER JOIN dbo.MachineShiftOccurrenceRoster AS r WITH (HOLDLOCK)
                    ON r.MachineShiftOccurrenceRosterRowId = o.MachineShiftOccurrenceRosterRowId
                WHERE r.MachineId = @MachineId
                  AND o.ShiftScheduleAssignmentId = @AssignmentId
                  AND o.ShiftId = @ShiftId
                  AND o.ShiftStartsAtUtc = @Starts
                  AND o.ShiftEndsAtUtc = @Ends
            )
                THROW 51024, 'Exact machine-shift roster occurrence is unavailable.', 1;

            IF EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntry WITH (UPDLOCK, HOLDLOCK)
                       WHERE EntryId = @EntryId)
            BEGIN
                IF NOT EXISTS
                (
                    SELECT 1 FROM dbo.SupervisorProductionEntry
                    WHERE EntryId = @EntryId AND MachineId = @MachineId
                      AND ShiftScheduleAssignmentId = @AssignmentId AND ShiftId = @ShiftId
                      AND ShiftStartsAtUtc = @Starts AND ShiftEndsAtUtc = @Ends
                      AND ProductionOrder = @ProductionOrder AND PartNumber = @PartNumber
                      AND Operation = @Operation AND
                          ((Note IS NULL AND @Note IS NULL) OR Note = @Note)
                      AND EnteredBy = @EnteredBy AND RecordedAtUtc = @RecordedAt
                )
                    THROW 51025, 'Entry ID already belongs to a different declaration.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                INSERT INTO dbo.SupervisorProductionEntry
                    (EntryId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                     ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionOrder, PartNumber,
                     Operation, Note, EnteredBy, RecordedAtUtc)
                VALUES (@EntryId, @MachineId, @AssignmentId, @ShiftId,
                        @Starts, @Ends, @ProductionOrder, @PartNumber,
                        @Operation, @Note, @EnteredBy, @RecordedAt);
                SELECT CAST(1 AS bit);
            END
            """;
        command.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = entry.EntryId;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = entry.MachineId;
        AddText(command, "@AssignmentId", entry.ShiftScheduleAssignmentId, 200);
        AddText(command, "@ShiftId", entry.ShiftId, 200);
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = entry.ShiftStartsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = entry.ShiftEndsAtUtc;
        AddText(command, "@ProductionOrder", entry.ProductionOrder, 200);
        AddText(command, "@PartNumber", entry.PartNumber, 200);
        AddText(command, "@Operation", entry.Operation, 200);
        AddText(command, "@Note", entry.Note, 2000);
        AddText(command, "@EnteredBy", entry.EnteredBy, 200);
        command.Parameters.Add("@RecordedAt", SqlDbType.DateTimeOffset).Value = entry.RecordedAtUtc;
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Supervisor insert returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    private static void Check(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Required supervisor declaration text is missing or too long.");
    }

    private static void AddText(SqlCommand command, string name, string? value, int maximum) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, maximum).Value = (object?)value ?? DBNull.Value;
}
