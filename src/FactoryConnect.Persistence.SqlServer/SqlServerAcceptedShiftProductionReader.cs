using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Read-only projection of an explicitly published accepted shift production fact.
/// This reader never qualifies historical metric inputs or supervisor declarations.
/// </summary>
public sealed record AcceptedShiftProductionRead(
    Guid PublicationId, Guid MachineId, string ShiftScheduleAssignmentId,
    string ShiftId, DateTimeOffset ShiftStartsAtUtc, DateTimeOffset ShiftEndsAtUtc,
    string? ProductionOrder, string? PartNumber, string? Operation,
    string QuantityUnit, long AcceptedProducedQuantity, string Coverage,
    string AuthorityReference, DateTimeOffset PublishedAtUtc);

public sealed class SqlServerAcceptedShiftProductionReader
{
    private readonly string _connectionString;

    public SqlServerAcceptedShiftProductionReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<AcceptedShiftProductionRead?> ReadAsync(
        Guid machineId, string assignmentId, string shiftId,
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || string.IsNullOrWhiteSpace(assignmentId) ||
            string.IsNullOrWhiteSpace(shiftId) || assignmentId.Length > 200 ||
            shiftId.Length > 200 || startsAtUtc.Offset != TimeSpan.Zero ||
            endsAtUtc.Offset != TimeSpan.Zero || endsAtUtc <= startsAtUtc)
            throw new ArgumentException("Invalid exact shift occurrence.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadCoreAsync(connection, null, machineId, assignmentId, shiftId, startsAtUtc, endsAtUtc, cancellationToken);
    }

    internal static async Task<AcceptedShiftProductionRead?> ReadCoreAsync(SqlConnection connection, SqlTransaction? transaction,
        Guid machineId, string assignmentId, string shiftId,
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
                   ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionOrder, PartNumber,
                   Operation, QuantityUnit, AcceptedProducedQuantity, Coverage,
                   AuthorityReference, PublishedAtUtc
            FROM dbo.SupervisorAcceptedShiftProduction
            WHERE MachineId = @Machine AND ShiftScheduleAssignmentId = @Assignment
              AND ShiftId = @Shift AND ShiftStartsAtUtc = @Starts AND ShiftEndsAtUtc = @Ends;
            """;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@Assignment", SqlDbType.NVarChar, 200).Value = assignmentId;
        command.Parameters.Add("@Shift", SqlDbType.NVarChar, 200).Value = shiftId;
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = startsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = endsAtUtc;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = new AcceptedShiftProductionRead(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9), reader.GetInt64(10), reader.GetString(11),
            reader.GetString(12), reader.GetFieldValue<DateTimeOffset>(13));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Conflicting accepted shift production publications.");
        if (result.PublicationId == Guid.Empty || result.AcceptedProducedQuantity < 0 ||
            result.Coverage != "Complete" || string.IsNullOrWhiteSpace(result.AuthorityReference) ||
            string.IsNullOrWhiteSpace(result.QuantityUnit))
            throw new InvalidOperationException("Accepted shift production publication is invalid or incomplete.");
        return result;
    }
}
