using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed record SupervisorCycleContext(Guid MachineId, string AssignmentId, string ShiftId,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, string? ProductionOrder,
    string PartNumber, string? Operation);

public sealed class SqlServerSupervisorCycleDefaultStore(string connectionString)
{
    private const string Predicate = """
        MachineId=@Machine AND ShiftScheduleAssignmentId=@Assignment AND ShiftId=@Shift
        AND ShiftStartsAtUtc=@Starts AND ShiftEndsAtUtc=@Ends AND PartNumber=@Part
        AND ((ProductionOrder=@Order) OR (ProductionOrder IS NULL AND @Order IS NULL))
        AND ((Operation=@Operation) OR (Operation IS NULL AND @Operation IS NULL))
        """;
    private const string Columns = """
        SnapshotId, MachineId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc,
        ShiftEndsAtUtc, ProductionOrder, PartNumber, Operation, PartSeconds, OperationSeconds, CapturedAtUtc
        """;

    public async Task<SupervisorCycleDefaultSnapshot?> ReadAsync(SupervisorCycleContext context,
        CancellationToken cancellationToken = default)
    {
        Validate(context);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadCoreAsync(connection, null, context, cancellationToken);
    }

    // Capture only during the actual shift; never reconstruct past defaults from today's masters.
    public async Task<SupervisorCycleDefaultSnapshot?> CaptureAsync(SupervisorCycleContext context,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Validate(context);
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("Capture time must be UTC.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @Result int;
            EXEC @Result = sp_getapplock @Resource='SupervisorCycleDefaultCapture',
                @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
            IF @Result < 0 THROW 51146, 'Cycle default capture lock unavailable.', 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var existing = await ReadCoreAsync(connection, transaction, context, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        if (now < context.StartsAtUtc || now >= context.EndsAtUtc)
            throw new InvalidOperationException("Cannot capture a new default outside the production shift.");
        command.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM dbo.MachineShiftOccurrenceRosterOccurrence o
                JOIN dbo.MachineShiftOccurrenceRoster r
                  ON r.MachineShiftOccurrenceRosterRowId=o.MachineShiftOccurrenceRosterRowId
                WHERE r.MachineId=@Machine AND o.ShiftScheduleAssignmentId=@Assignment
                  AND o.ShiftId=@Shift AND o.ShiftStartsAtUtc=@Starts AND o.ShiftEndsAtUtc=@Ends)
                THROW 51147, 'Exact shift occurrence not present.', 1;
            INSERT dbo.SupervisorCycleDefaultSnapshot
                (SnapshotId, MachineId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc,
                 ShiftEndsAtUtc, ProductionOrder, PartNumber, Operation, PartSeconds, OperationSeconds, CapturedAtUtc)
            SELECT NEWID(), @Machine, @Assignment, @Shift, @Starts, @Ends, @Order,
                p.PartNumber, @Operation, p.IdealCycleSeconds, o.IdealCycleSeconds, @Now
            FROM dbo.PartMaster p
            LEFT JOIN dbo.OperationMaster o ON o.PartNumber=p.PartNumber AND o.OperationCode=@Operation AND o.IsActive=1
            WHERE p.PartNumber=@Part AND p.IsActive=1
              AND (@Operation IS NULL OR o.OperationCode IS NOT NULL);
            """;
        AddParameters(command, context);
        command.Parameters.Add("@Now", SqlDbType.DateTimeOffset).Value = now;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var captured = await ReadCoreAsync(connection, transaction, context, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return captured;
    }

    private static async Task<SupervisorCycleDefaultSnapshot?> ReadCoreAsync(SqlConnection connection,
        SqlTransaction? transaction, SupervisorCycleContext context, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TOP (2) " + Columns + " FROM dbo.SupervisorCycleDefaultSnapshot WHERE " + Predicate;
        AddParameters(command, context);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var snapshot = new SupervisorCycleDefaultSnapshot(reader.GetGuid(0), reader.GetGuid(1),
            reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetDecimal(9),
            reader.IsDBNull(10) ? null : reader.GetDecimal(10), reader.GetFieldValue<DateTimeOffset>(11));
        if (await reader.ReadAsync(cancellationToken) || snapshot.AssignmentMismatch(context))
            throw new InvalidOperationException("Conflicting cycle default context.");
        return snapshot;
    }

    public static void Validate(SupervisorCycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        static bool Key(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 200 && s == s.Trim();
        if (context.MachineId == Guid.Empty || !Key(context.AssignmentId) || !Key(context.ShiftId) ||
            !Key(context.PartNumber) || (context.ProductionOrder is not null && !Key(context.ProductionOrder)) ||
            (context.Operation is not null && !Key(context.Operation)) ||
            context.StartsAtUtc.Offset != TimeSpan.Zero || context.EndsAtUtc.Offset != TimeSpan.Zero ||
            context.EndsAtUtc <= context.StartsAtUtc || context.EndsAtUtc - context.StartsAtUtc > TimeSpan.FromDays(1))
            throw new ArgumentException("Invalid cycle default context.");
    }

    private static void AddParameters(SqlCommand command, SupervisorCycleContext context)
    {
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = context.MachineId;
        command.Parameters.Add("@Starts", SqlDbType.DateTimeOffset).Value = context.StartsAtUtc;
        command.Parameters.Add("@Ends", SqlDbType.DateTimeOffset).Value = context.EndsAtUtc;
        void Text(string name, string? value) => command.Parameters.Add(name, SqlDbType.NVarChar, 200).Value = (object?)value ?? DBNull.Value;
        Text("@Assignment", context.AssignmentId); Text("@Shift", context.ShiftId);
        Text("@Order", context.ProductionOrder); Text("@Part", context.PartNumber); Text("@Operation", context.Operation);
    }
}

internal static class SupervisorCycleSnapshotContextExtensions
{
    internal static bool AssignmentMismatch(this SupervisorCycleDefaultSnapshot snapshot, SupervisorCycleContext context)
        => snapshot.MachineId != context.MachineId || snapshot.ShiftScheduleAssignmentId != context.AssignmentId ||
           snapshot.ShiftId != context.ShiftId || snapshot.ShiftStartsAtUtc != context.StartsAtUtc ||
           snapshot.ShiftEndsAtUtc != context.EndsAtUtc || snapshot.ProductionOrder != context.ProductionOrder ||
           snapshot.PartNumber != context.PartNumber || snapshot.Operation != context.Operation;
}
