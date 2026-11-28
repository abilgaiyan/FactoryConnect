using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

// Persistence seam for a trusted independent verifier, not a supervisor-facing operation.
// The caller must establish complete coverage and population reconciliation independently.
public sealed class SqlServerSupervisorVerificationWriter(string connectionString)
{
    public async Task<bool> VerifyAsync(Guid receiptId, Guid verificationId, long verifiedQuantity,
        string populationKey, string authorityReference, CancellationToken cancellationToken = default)
    {
        if (receiptId == Guid.Empty || verificationId == Guid.Empty || verifiedQuantity < 0)
            throw new ArgumentException("Valid verification identity and quantity required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(populationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityReference);
        if (populationKey.Length > 200 || authorityReference.Length > 200 ||
            populationKey != populationKey.Trim() || authorityReference != authorityReference.Trim())
            throw new ArgumentException("Invalid verification reference.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @now datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
            INSERT dbo.SupervisorAcceptedProductionVerification
                (VerificationId, ReceiptId, VerifiedProducedQuantity, Coverage, AuthorityReference, VerifiedAtUtc)
            SELECT @verification, q.ReceiptId, @quantity, N'Complete', @authority, @now
            FROM dbo.SupervisorProductionQualificationReceipt q
            JOIN dbo.SupervisorProductionEntryRevision r ON r.RevisionId = q.EvidenceId
            JOIN dbo.SupervisorProductionEntry e ON e.EntryId = r.EntryId
            JOIN dbo.SupervisorProductionEntryRevisionReview v ON v.RevisionId = r.RevisionId
            WHERE q.ReceiptId = @receipt AND q.EvidenceKind = N'SupervisorRevision'
              AND q.QualificationDecision = N'Qualified' AND q.QuantityUnit = N'pieces'
              AND q.PopulationKey = @population AND q.AuthorityReference <> @authority
              AND q.ProducedQuantity = @quantity AND q.GoodQuantity = r.GoodQuantity
              AND q.RejectedQuantity = r.RejectedQuantity
              AND q.MachineId = e.MachineId AND q.ShiftScheduleAssignmentId = e.ShiftScheduleAssignmentId
              AND q.ShiftId = e.ShiftId AND q.ShiftStartsAtUtc = e.ShiftStartsAtUtc
              AND q.ShiftEndsAtUtc = e.ShiftEndsAtUtc AND q.ProductionOrder = r.ProductionOrder
              AND q.PartNumber = r.PartNumber AND q.Operation = r.Operation
              AND v.Decision = N'Approved' AND r.RevisedAtUtc <= v.ReviewedAtUtc
              AND v.ReviewedAtUtc <= q.QualifiedAtUtc AND q.QualifiedAtUtc <= @now
              AND e.ShiftEndsAtUtc <= q.QualifiedAtUtc
              AND NOT EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntryRevision newer
                  WHERE newer.EntryId = e.EntryId AND newer.RevisionNumber > r.RevisionNumber)
              AND NOT EXISTS (SELECT 1 FROM dbo.SupervisorAcceptedProductionVerification existing
                  WHERE existing.ReceiptId = q.ReceiptId);
            """;
        command.Parameters.Add("@receipt", SqlDbType.UniqueIdentifier).Value = receiptId;
        command.Parameters.Add("@verification", SqlDbType.UniqueIdentifier).Value = verificationId;
        command.Parameters.Add("@quantity", SqlDbType.BigInt).Value = verifiedQuantity;
        command.Parameters.Add("@population", SqlDbType.NVarChar, 200).Value = populationKey;
        command.Parameters.Add("@authority", SqlDbType.NVarChar, 200).Value = authorityReference;
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1;
    }
}
