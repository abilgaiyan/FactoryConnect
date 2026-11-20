using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Explicitly supplied evidence verifier. Implementations must validate the source
/// evidence, qualification authority, and exact receipt contents against trusted data.
/// No default or permissive implementation is provided.
/// </summary>
public interface IProductionQualificationEvidenceVerifier
{
    Task<bool> VerifyAsync(ProductionQualificationReceiptRead receipt,
        CancellationToken cancellationToken);
}

/// <summary>
/// An unregistered SQL writer boundary. This class does not authenticate the caller:
/// deployment must supply a trusted verifier and restrict SQL INSERT permissions.
/// </summary>
public sealed class SqlServerProductionQualificationReceiptWriter
{
    private readonly string _connectionString;
    private readonly IProductionQualificationEvidenceVerifier _verifier;

    public SqlServerProductionQualificationReceiptWriter(
        string connectionString, IProductionQualificationEvidenceVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _connectionString = connectionString;
    }

    public async Task AppendAsync(ProductionQualificationReceiptRead receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ReceiptId == Guid.Empty || receipt.EvidenceId == Guid.Empty ||
            receipt.MachineId == Guid.Empty ||
            receipt.EvidenceKind is not ("CncCounter" or "SupervisorRevision") ||
            receipt.Decision is not ("Qualified" or "Rejected") ||
            string.IsNullOrWhiteSpace(receipt.PopulationKey) || receipt.PopulationKey.Length > 200 ||
            string.IsNullOrWhiteSpace(receipt.AssignmentId) || receipt.AssignmentId.Length > 200 ||
            string.IsNullOrWhiteSpace(receipt.ShiftId) || receipt.ShiftId.Length > 200 ||
            string.IsNullOrWhiteSpace(receipt.QuantityUnit) || receipt.QuantityUnit.Length > 50 ||
            string.IsNullOrWhiteSpace(receipt.AuthorityReference) || receipt.AuthorityReference.Length > 200 ||
            receipt.ProductionOrder?.Length > 200 || receipt.PartNumber?.Length > 200 ||
            receipt.Operation?.Length > 200 ||
            receipt.StartsAtUtc.Offset != TimeSpan.Zero ||
            receipt.EndsAtUtc.Offset != TimeSpan.Zero ||
            receipt.QualifiedAtUtc.Offset != TimeSpan.Zero ||
            receipt.EndsAtUtc <= receipt.StartsAtUtc ||
            receipt.EndsAtUtc - receipt.StartsAtUtc > TimeSpan.FromDays(1) ||
            receipt.Produced < 0 || receipt.Good < 0 || receipt.Rejected < 0 ||
            receipt.Good > receipt.Produced || receipt.Rejected != receipt.Produced - receipt.Good)
            throw new ArgumentException("Invalid qualification receipt.", nameof(receipt));

        if (!await _verifier.VerifyAsync(receipt, cancellationToken))
            throw new InvalidOperationException("Qualification evidence verification failed.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SET XACT_ABORT ON;
            IF EXISTS (
                SELECT 1 FROM dbo.SupervisorProductionQualificationReceipt WITH (UPDLOCK, HOLDLOCK)
                WHERE (EvidenceKind = @Kind AND EvidenceId = @Evidence)
                   OR ReceiptId = @Receipt)
                THROW 51140, 'Qualification receipt identity already exists.', 1;
            INSERT INTO dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 ProductionOrder, PartNumber, Operation, QuantityUnit, ProducedQuantity,
                 GoodQuantity, RejectedQuantity, QualificationDecision, AuthorityReference,
                 QualifiedAtUtc)
            VALUES (@Receipt, @Evidence, @Kind, @Population, @Machine,
                    @Assignment, @Shift, @Starts, @Ends, @Order, @Part, @Operation,
                    @Unit, @Produced, @Good, @Rejected, @Decision, @Authority, @Qualified);
            """;
        void Add(string name, SqlDbType type, object? value, int size = 0)
        {
            var parameter = size == 0 ? command.Parameters.Add(name, type)
                : command.Parameters.Add(name, type, size);
            parameter.Value = value ?? DBNull.Value;
        }
        Add("@Receipt", SqlDbType.UniqueIdentifier, receipt.ReceiptId);
        Add("@Evidence", SqlDbType.UniqueIdentifier, receipt.EvidenceId);
        Add("@Kind", SqlDbType.NVarChar, receipt.EvidenceKind, 30);
        Add("@Population", SqlDbType.NVarChar, receipt.PopulationKey, 200);
        Add("@Machine", SqlDbType.UniqueIdentifier, receipt.MachineId);
        Add("@Assignment", SqlDbType.NVarChar, receipt.AssignmentId, 200);
        Add("@Shift", SqlDbType.NVarChar, receipt.ShiftId, 200);
        Add("@Starts", SqlDbType.DateTimeOffset, receipt.StartsAtUtc);
        Add("@Ends", SqlDbType.DateTimeOffset, receipt.EndsAtUtc);
        Add("@Order", SqlDbType.NVarChar, receipt.ProductionOrder, 200);
        Add("@Part", SqlDbType.NVarChar, receipt.PartNumber, 200);
        Add("@Operation", SqlDbType.NVarChar, receipt.Operation, 200);
        Add("@Unit", SqlDbType.NVarChar, receipt.QuantityUnit, 50);
        Add("@Produced", SqlDbType.BigInt, receipt.Produced);
        Add("@Good", SqlDbType.BigInt, receipt.Good);
        Add("@Rejected", SqlDbType.BigInt, receipt.Rejected);
        Add("@Decision", SqlDbType.NVarChar, receipt.Decision, 20);
        Add("@Authority", SqlDbType.NVarChar, receipt.AuthorityReference, 200);
        Add("@Qualified", SqlDbType.DateTimeOffset, receipt.QualifiedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
