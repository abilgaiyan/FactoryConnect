using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

// Trusted workflow seam. Does not establish independent verifier identity or canonical admission.
public sealed class SqlServerVerifiedSupervisorPublicationWriter(string connectionString)
{
    public async Task<bool> PublishAsync(Guid receiptId, Guid verificationId, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        if (receiptId == Guid.Empty || verificationId == Guid.Empty || publicationId == Guid.Empty)
            throw new ArgumentException("Exact receipt, verification and publication identities required.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.SupervisorProductionQualificationReceipt q WITH (UPDLOCK, HOLDLOCK)
            JOIN dbo.SupervisorAcceptedProductionVerification v ON v.ReceiptId = q.ReceiptId
            JOIN dbo.SupervisorProductionEntryRevision r ON r.RevisionId = q.EvidenceId
            JOIN dbo.SupervisorProductionEntry e ON e.EntryId = r.EntryId
            JOIN dbo.SupervisorProductionEntryRevisionReview review ON review.RevisionId = r.RevisionId
            WHERE q.ReceiptId = @receipt AND v.VerificationId = @verification
              AND q.EvidenceKind = N'SupervisorRevision' AND q.QualificationDecision = N'Qualified'
              AND q.QuantityUnit = N'pieces' AND v.Coverage = N'Complete'
              AND q.AuthorityReference <> v.AuthorityReference
              AND q.ProducedQuantity = v.VerifiedProducedQuantity
              AND q.GoodQuantity = r.GoodQuantity AND q.RejectedQuantity = r.RejectedQuantity
              AND q.MachineId = e.MachineId AND q.ShiftScheduleAssignmentId = e.ShiftScheduleAssignmentId
              AND q.ShiftId = e.ShiftId AND q.ShiftStartsAtUtc = e.ShiftStartsAtUtc
              AND q.ShiftEndsAtUtc = e.ShiftEndsAtUtc AND q.ProductionOrder = r.ProductionOrder
              AND q.PartNumber = r.PartNumber AND q.Operation = r.Operation
              AND review.Decision = N'Approved' AND r.RevisedAtUtc <= review.ReviewedAtUtc
              AND review.ReviewedAtUtc <= q.QualifiedAtUtc AND e.ShiftEndsAtUtc <= q.QualifiedAtUtc
              AND q.QualifiedAtUtc <= v.VerifiedAtUtc
              AND v.VerifiedAtUtc <= TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
              AND NOT EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntryRevision newer
                  WHERE newer.EntryId = r.EntryId AND newer.RevisionNumber > r.RevisionNumber);
            """;
        command.Parameters.Add("@receipt", SqlDbType.UniqueIdentifier).Value = receiptId;
        command.Parameters.Add("@verification", SqlDbType.UniqueIdentifier).Value = verificationId;
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await using var publish = new SqlCommand("dbo.PublishSupervisorAcceptedProduction", connection, transaction)
        {
            CommandType = CommandType.StoredProcedure
        };
        publish.Parameters.Add("@ReceiptId", SqlDbType.UniqueIdentifier).Value = receiptId;
        publish.Parameters.Add("@VerificationId", SqlDbType.UniqueIdentifier).Value = verificationId;
        publish.Parameters.Add("@PublicationId", SqlDbType.UniqueIdentifier).Value = publicationId;
        await publish.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
