using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Reads supervisor declarations without interpreting them as CNC evidence or OEE facts.
/// </summary>
public sealed class SqlServerSupervisorProductionEntryReader
{
    private readonly string _connectionString;

    public SqlServerSupervisorProductionEntryReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<SupervisorProductionEntryRecord>> ListAsync(
        Guid machineId,
        DateTimeOffset shiftStartsAtUtc,
        DateTimeOffset shiftEndsAtUtc,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty) throw new ArgumentException("Machine is required.", nameof(machineId));
        if (shiftStartsAtUtc.Offset != TimeSpan.Zero || shiftEndsAtUtc.Offset != TimeSpan.Zero ||
            shiftStartsAtUtc >= shiftEndsAtUtc)
            throw new ArgumentException("An ordered UTC shift interval is required.");
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@Limit)
                EntryId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionOrder, PartNumber,
                Operation, Note, EnteredBy, RecordedAtUtc, GoodQuantity, RejectedQuantity
            FROM dbo.SupervisorProductionEntry
            WHERE MachineId = @MachineId
              AND ShiftStartsAtUtc = @ShiftStartsAtUtc
              AND ShiftEndsAtUtc = @ShiftEndsAtUtc
            ORDER BY RecordedAtUtc DESC, EntryId DESC;
            """;
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@ShiftStartsAtUtc", SqlDbType.DateTimeOffset).Value = shiftStartsAtUtc;
        command.Parameters.Add("@ShiftEndsAtUtc", SqlDbType.DateTimeOffset).Value = shiftEndsAtUtc;
        var result = new List<SupervisorProductionEntryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SupervisorProductionEntryRecord(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13)));
        }
        return result;
    }
}

public sealed record SupervisorProductionEntryRecord(
    Guid EntryId,
    Guid MachineId,
    string ShiftScheduleAssignmentId,
    string ShiftId,
    DateTimeOffset ShiftStartsAtUtc,
    DateTimeOffset ShiftEndsAtUtc,
    string ProductionOrder,
    string PartNumber,
    string Operation,
    string? Note,
    string EnteredBy,
    DateTimeOffset RecordedAtUtc,
    long? GoodQuantity = null,
    long? RejectedQuantity = null);
