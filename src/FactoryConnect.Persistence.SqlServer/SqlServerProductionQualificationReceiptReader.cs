using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Durable qualification receipt read model. A stored "Qualified" decision is not
/// independently authenticated authority and must not directly authorize publication.
/// </summary>
public sealed record ProductionQualificationReceiptRead(
    Guid ReceiptId, Guid EvidenceId, string EvidenceKind, string PopulationKey,
    Guid MachineId, string AssignmentId, string ShiftId,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
    string? ProductionOrder, string? PartNumber, string? Operation,
    string QuantityUnit, long Produced, long Good, long Rejected,
    string Decision, string AuthorityReference, DateTimeOffset QualifiedAtUtc);

public sealed class SqlServerProductionQualificationReceiptReader
{
    private readonly string _connectionString;

    public SqlServerProductionQualificationReceiptReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<ProductionQualificationReceiptRead?> ReadAsync(
        string evidenceKind, Guid evidenceId, CancellationToken cancellationToken = default)
    {
        if (evidenceKind is not ("CncCounter" or "SupervisorRevision") || evidenceId == Guid.Empty)
            throw new ArgumentException("Invalid qualification evidence identity.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                   ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                   ProductionOrder, PartNumber, Operation, QuantityUnit,
                   ProducedQuantity, GoodQuantity, RejectedQuantity,
                   QualificationDecision, AuthorityReference, QualifiedAtUtc
            FROM dbo.SupervisorProductionQualificationReceipt
            WHERE EvidenceKind = @Kind AND EvidenceId = @Evidence;
            """;
        command.Parameters.Add("@Kind", SqlDbType.NVarChar, 30).Value = evidenceKind;
        command.Parameters.Add("@Evidence", SqlDbType.UniqueIdentifier).Value = evidenceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = new ProductionQualificationReceiptRead(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            reader.GetGuid(4), reader.GetString(5), reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetString(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
            reader.GetString(16), reader.GetString(17),
            reader.GetFieldValue<DateTimeOffset>(18));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Conflicting qualification receipts.");
        Validate(result, evidenceKind, evidenceId);
        return result;
    }

    private static void Validate(ProductionQualificationReceiptRead receipt, string kind, Guid id)
    {
        if (receipt.ReceiptId == Guid.Empty || receipt.EvidenceId != id ||
            !StringComparer.Ordinal.Equals(receipt.EvidenceKind, kind) ||
            receipt.MachineId == Guid.Empty ||
            string.IsNullOrWhiteSpace(receipt.PopulationKey) ||
            string.IsNullOrWhiteSpace(receipt.AssignmentId) ||
            string.IsNullOrWhiteSpace(receipt.ShiftId) ||
            string.IsNullOrWhiteSpace(receipt.QuantityUnit) ||
            string.IsNullOrWhiteSpace(receipt.AuthorityReference) ||
            receipt.PopulationKey.Length > 200 || receipt.AssignmentId.Length > 200 ||
            receipt.ShiftId.Length > 200 || receipt.QuantityUnit.Length > 50 ||
            receipt.AuthorityReference.Length > 200 ||
            receipt.StartsAtUtc.Offset != TimeSpan.Zero ||
            receipt.EndsAtUtc.Offset != TimeSpan.Zero ||
            receipt.QualifiedAtUtc.Offset != TimeSpan.Zero ||
            receipt.EndsAtUtc <= receipt.StartsAtUtc ||
            receipt.EndsAtUtc - receipt.StartsAtUtc > TimeSpan.FromDays(1) ||
            receipt.Produced < 0 || receipt.Good < 0 || receipt.Rejected < 0 ||
            receipt.Good > receipt.Produced ||
            receipt.Rejected != receipt.Produced - receipt.Good ||
            receipt.Decision is not ("Qualified" or "Rejected"))
            throw new InvalidOperationException("Invalid durable qualification receipt.");
    }
}
