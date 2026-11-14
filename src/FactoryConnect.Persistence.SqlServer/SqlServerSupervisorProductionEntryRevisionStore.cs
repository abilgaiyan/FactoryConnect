using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Append-only local correction history; never qualifies production quantity.</summary>
public sealed record SupervisorProductionEntryRevisionRecord(
    Guid RevisionId, Guid EntryId, Guid? PreviousRevisionId, int RevisionNumber,
    string ProductionOrder, string PartNumber, string Operation, string? Note,
    long? GoodQuantity, long? RejectedQuantity, string CorrectionReason,
    string RevisedBy, DateTimeOffset RevisedAtUtc);

public sealed class SqlServerSupervisorProductionEntryRevisionStore
{
    private readonly string _connectionString;

    public SqlServerSupervisorProductionEntryRevisionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<bool> AppendAsync(Guid machineId, Guid? expectedRevisionId,
        SupervisorProductionEntryRevisionRecord revision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (machineId == Guid.Empty || revision.EntryId == Guid.Empty || revision.RevisionId == Guid.Empty ||
            revision.RevisionNumber < 1 || revision.RevisedAtUtc.Offset != TimeSpan.Zero ||
            (revision.GoodQuantity is null) != (revision.RejectedQuantity is null) ||
            revision.GoodQuantity < 0 || revision.RejectedQuantity < 0)
            throw new ArgumentException("Invalid revision identity, quantities or UTC time.");
        Check(revision.ProductionOrder, 200);
        Check(revision.PartNumber, 200);
        Check(revision.Operation, 200);
        Check(revision.CorrectionReason, 2000);
        Check(revision.RevisedBy, 200);
        if (revision.Note?.Length > 2000)
            throw new ArgumentException("Revision note too long.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntry WITH (UPDLOCK, HOLDLOCK)
                           WHERE EntryId = @EntryId AND MachineId = @MachineId)
                THROW 51030, 'Revision declaration not found for machine.', 1;

            IF EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntryRevision WITH (UPDLOCK, HOLDLOCK)
                       WHERE RevisionId = @RevisionId)
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.SupervisorProductionEntryRevision
                    WHERE RevisionId = @RevisionId AND EntryId = @EntryId
                      AND RevisionNumber = @Number
                      AND ((PreviousRevisionId IS NULL AND @Expected IS NULL) OR PreviousRevisionId = @Expected)
                      AND ProductionOrder = @Order AND PartNumber = @Part AND Operation = @Operation
                      AND ((Note IS NULL AND @Note IS NULL) OR Note = @Note)
                      AND ((GoodQuantity IS NULL AND @Good IS NULL) OR GoodQuantity = @Good)
                      AND ((RejectedQuantity IS NULL AND @Rejected IS NULL) OR RejectedQuantity = @Rejected)
                      AND CorrectionReason = @Reason AND RevisedBy = @Actor)
                    THROW 51031, 'Revision ID reused with different contents.', 1;
                SELECT CAST(0 AS bit);
            END
            ELSE
            BEGIN
                DECLARE @Head uniqueidentifier;
                DECLARE @NumberAtHead int;
                SELECT TOP (1) @Head = RevisionId, @NumberAtHead = RevisionNumber
                FROM dbo.SupervisorProductionEntryRevision WITH (UPDLOCK, HOLDLOCK)
                WHERE EntryId = @EntryId ORDER BY RevisionNumber DESC;
                IF ((@Head IS NULL AND @Expected IS NOT NULL)
                    OR (@Head IS NOT NULL AND (@Expected IS NULL OR @Head <> @Expected))
                    OR @Number <> ISNULL(@NumberAtHead, 0) + 1)
                    THROW 51032, 'Stale expected revision head.', 1;
                INSERT INTO dbo.SupervisorProductionEntryRevision
                    (RevisionId, EntryId, PreviousRevisionId, RevisionNumber,
                     ProductionOrder, PartNumber, Operation, Note, GoodQuantity,
                     RejectedQuantity, CorrectionReason, RevisedBy, RevisedAtUtc)
                VALUES (@RevisionId, @EntryId, @Expected, @Number,
                        @Order, @Part, @Operation, @Note, @Good,
                        @Rejected, @Reason, @Actor, @At);
                SELECT CAST(1 AS bit);
            END
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = revision.EntryId;
        command.Parameters.Add("@RevisionId", SqlDbType.UniqueIdentifier).Value = revision.RevisionId;
        command.Parameters.Add("@Expected", SqlDbType.UniqueIdentifier).Value = (object?)expectedRevisionId ?? DBNull.Value;
        command.Parameters.Add("@Number", SqlDbType.Int).Value = revision.RevisionNumber;
        AddText(command, "@Order", revision.ProductionOrder, 200);
        AddText(command, "@Part", revision.PartNumber, 200);
        AddText(command, "@Operation", revision.Operation, 200);
        AddText(command, "@Note", revision.Note, 2000);
        command.Parameters.Add("@Good", SqlDbType.BigInt).Value = (object?)revision.GoodQuantity ?? DBNull.Value;
        command.Parameters.Add("@Rejected", SqlDbType.BigInt).Value = (object?)revision.RejectedQuantity ?? DBNull.Value;
        AddText(command, "@Reason", revision.CorrectionReason, 2000);
        AddText(command, "@Actor", revision.RevisedBy, 200);
        command.Parameters.Add("@At", SqlDbType.DateTimeOffset).Value = revision.RevisedAtUtc;
        var inserted = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Revision append returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<SupervisorProductionEntryRevisionRecord>> ListAsync(
        Guid machineId, Guid entryId, CancellationToken cancellationToken = default)
    {
        if (machineId == Guid.Empty || entryId == Guid.Empty)
            throw new ArgumentException("Machine and entry are required.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.RevisionId, r.EntryId, r.PreviousRevisionId, r.RevisionNumber,
                   r.ProductionOrder, r.PartNumber, r.Operation, r.Note,
                   r.GoodQuantity, r.RejectedQuantity, r.CorrectionReason, r.RevisedBy, r.RevisedAtUtc
            FROM dbo.SupervisorProductionEntryRevision AS r
            INNER JOIN dbo.SupervisorProductionEntry AS e ON e.EntryId = r.EntryId
            WHERE e.MachineId = @MachineId AND e.EntryId = @EntryId
            ORDER BY r.RevisionNumber ASC;
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = entryId;
        var result = new List<SupervisorProductionEntryRevisionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.GetString(10), reader.GetString(11),
                reader.GetFieldValue<DateTimeOffset>(12)));
        return result;
    }

    private static void Check(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new ArgumentException("Required correction text missing or too long.");
    }

    private static void AddText(SqlCommand command, string name, string? value, int max) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, max).Value = (object?)value ?? DBNull.Value;
}
