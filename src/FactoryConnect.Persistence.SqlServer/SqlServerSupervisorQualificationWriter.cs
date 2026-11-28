using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

// Trusted caller must independently establish the population and qualification authority.
// Not exposed to the local supervisor API; does not verify coverage or publish production.
public sealed class SqlServerSupervisorQualificationWriter(string connectionString)
{
    public async Task<bool> QualifyAsync(Guid entryId, Guid revisionId, Guid receiptId,
        string populationKey, string authorityReference, CancellationToken cancellationToken = default)
    {
        if (entryId == Guid.Empty || revisionId == Guid.Empty || receiptId == Guid.Empty)
            throw new ArgumentException("Exact entry, revision and receipt identities are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(populationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityReference);
        if (populationKey.Length > 200 || authorityReference.Length > 200 ||
            populationKey != populationKey.Trim() || authorityReference != authorityReference.Trim())
            throw new ArgumentException("Invalid population or authority reference.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @now datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 ProductionOrder, PartNumber, Operation, QuantityUnit, ProducedQuantity,
                 GoodQuantity, RejectedQuantity, QualificationDecision, AuthorityReference, QualifiedAtUtc)
            SELECT @receipt, r.RevisionId, N'SupervisorRevision', @population, e.MachineId,
                   e.ShiftScheduleAssignmentId, e.ShiftId, e.ShiftStartsAtUtc, e.ShiftEndsAtUtc,
                   r.ProductionOrder, r.PartNumber, r.Operation, N'pieces',
                   r.GoodQuantity + r.RejectedQuantity, r.GoodQuantity, r.RejectedQuantity,
                   N'Qualified', @authority, @now
            FROM dbo.SupervisorProductionEntry e
            JOIN dbo.SupervisorProductionEntryRevision r ON r.EntryId = e.EntryId
            JOIN dbo.SupervisorProductionEntryRevisionReview v ON v.RevisionId = r.RevisionId
            WHERE e.EntryId = @entry AND r.RevisionId = @revision
              AND v.Decision = N'Approved' AND r.RevisedAtUtc <= v.ReviewedAtUtc
              AND v.ReviewedAtUtc <= @now AND e.ShiftEndsAtUtc <= @now
              AND r.GoodQuantity IS NOT NULL AND r.RejectedQuantity IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntryRevision newer
                  WHERE newer.EntryId = e.EntryId AND newer.RevisionNumber > r.RevisionNumber)
              AND NOT EXISTS (SELECT 1 FROM dbo.SupervisorProductionQualificationReceipt q
                  WHERE q.EvidenceKind = N'SupervisorRevision' AND q.EvidenceId = r.RevisionId);
            """;
        command.Parameters.Add("@entry", SqlDbType.UniqueIdentifier).Value = entryId;
        command.Parameters.Add("@revision", SqlDbType.UniqueIdentifier).Value = revisionId;
        command.Parameters.Add("@receipt", SqlDbType.UniqueIdentifier).Value = receiptId;
        command.Parameters.Add("@population", SqlDbType.NVarChar, 200).Value = populationKey;
        command.Parameters.Add("@authority", SqlDbType.NVarChar, 200).Value = authorityReference;
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1;
    }
}
