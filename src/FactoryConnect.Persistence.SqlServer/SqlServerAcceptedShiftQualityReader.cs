using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

public sealed record AcceptedShiftQualityRead(Guid PublicationId, Guid ReceiptId,
    Guid VerificationId, long ProducedQuantity, long GoodQuantity, long RejectedQuantity)
{
    public decimal? QualityRatio => ProducedQuantity == 0 ? null : (decimal)GoodQuantity / ProducedQuantity;
}

// Read-only evidence for a single accepted publication; not canonical metric admission.
public sealed class SqlServerAcceptedShiftQualityReader(string connectionString)
{
    public async Task<AcceptedShiftQualityRead?> ReadAsync(Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        if (publicationId == Guid.Empty)
            throw new ArgumentException("Publication identity is required.", nameof(publicationId));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var result = await ReadCoreAsync(connection, transaction, publicationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AcceptedShiftQualityRead?> ReadForShiftAsync(Guid machineId, string assignmentId,
        string shiftId, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        SqlServerSupervisorCycleDefaultStore.Validate(new SupervisorCycleContext(machineId,
            assignmentId, shiftId, startsAtUtc, endsAtUtc, null, "validation", null));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var publication = await SqlServerAcceptedShiftProductionReader.ReadCoreAsync(connection, transaction,
            machineId, assignmentId, shiftId, startsAtUtc, endsAtUtc, cancellationToken);
        var result = publication is null ? null : await ReadCoreAsync(connection, transaction,
            publication.PublicationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<AcceptedShiftQualityRead?> ReadCoreAsync(SqlConnection connection,
        SqlTransaction transaction, Guid publicationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.ReceiptId, v.VerificationId, r.ProducedQuantity, r.GoodQuantity, r.RejectedQuantity,
                   CASE WHEN r.QualificationDecision = N'Qualified' AND p.Coverage = N'Complete'
                     AND p.QuantityUnit = N'pieces' AND r.QuantityUnit = p.QuantityUnit
                     AND r.MachineId = p.MachineId AND r.ShiftScheduleAssignmentId = p.ShiftScheduleAssignmentId
                     AND r.ShiftId = p.ShiftId AND r.ShiftStartsAtUtc = p.ShiftStartsAtUtc
                     AND r.ShiftEndsAtUtc = p.ShiftEndsAtUtc
                     AND r.ProducedQuantity = p.AcceptedProducedQuantity
                     AND v.VerifiedProducedQuantity = r.ProducedQuantity AND v.Coverage = N'Complete'
                     AND v.AuthorityReference = p.AuthorityReference
                     AND r.QualifiedAtUtc <= v.VerifiedAtUtc AND v.VerifiedAtUtc <= p.PublishedAtUtc
                     THEN 1 ELSE 0 END
            FROM dbo.SupervisorAcceptedShiftProduction AS p
            JOIN dbo.SupervisorAcceptedShiftProductionSource AS s ON s.PublicationId = p.PublicationId
            JOIN dbo.SupervisorProductionQualificationReceipt AS r ON r.ReceiptId = s.ReceiptId
            LEFT JOIN dbo.SupervisorAcceptedProductionVerification AS v ON v.ReceiptId = r.ReceiptId
            WHERE p.PublicationId = @publication;
            """;
        command.Parameters.Add("@publication", SqlDbType.UniqueIdentifier).Value = publicationId;
        AcceptedShiftQualityRead? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(5) == 1 && !reader.IsDBNull(1))
                {
                    var produced = reader.GetInt64(2);
                    var good = reader.GetInt64(3);
                    var rejected = reader.GetInt64(4);
                    if (produced >= 0 && good >= 0 && good <= produced && rejected == produced - good)
                        result = new(publicationId, reader.GetGuid(0), reader.GetGuid(1), produced, good, rejected);
                }
                if (await reader.ReadAsync(cancellationToken))
                    result = null;
            }
        }
        return result;
    }
}
