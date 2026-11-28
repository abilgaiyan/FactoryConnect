using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorQualificationWriterTests
{
    [Theory]
    [InlineData("Approved", false, true)]
    [InlineData("Rejected", false, false)]
    [InlineData("Approved", true, false)]
    [InlineData("Missing", false, false)]
    public async Task OnlyCurrentApprovedRevisionQualifies(string decision, bool superseded, bool expected)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        var entry = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionEntry
                (EntryId, MachineId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc,
                 ShiftEndsAtUtc, ProductionOrder, PartNumber, Operation, EnteredBy, RecordedAtUtc)
            VALUES (@entry, NEWID(), N'assignment', N'shift', '2026-01-01T00:00:00+00:00',
                    '2026-01-01T08:00:00+00:00', N'WO', N'PART', N'OP', N'operator',
                    '2026-01-01T08:01:00+00:00');
            INSERT dbo.SupervisorProductionEntryRevision
                (RevisionId, EntryId, RevisionNumber, ProductionOrder, PartNumber, Operation,
                 GoodQuantity, RejectedQuantity, CorrectionReason, RevisedBy, RevisedAtUtc)
            VALUES (@revision, @entry, 1, N'WO', N'PART', N'OP', 10, 2, N'Correction',
                    N'operator', '2026-01-01T08:02:00+00:00');
            IF @decision <> N'Missing'
                INSERT dbo.SupervisorProductionEntryRevisionReview
                    (RevisionId, DecisionId, Decision, Reason, ReviewedBy, ReviewedAtUtc)
                VALUES (@revision, NEWID(), @decision, N'Review', N'reviewer',
                        '2026-01-01T08:03:00+00:00');
            IF @superseded = 1
                INSERT dbo.SupervisorProductionEntryRevision
                    (RevisionId, EntryId, PreviousRevisionId, RevisionNumber, ProductionOrder,
                     PartNumber, Operation, GoodQuantity, RejectedQuantity, CorrectionReason,
                     RevisedBy, RevisedAtUtc)
                VALUES (NEWID(), @entry, @revision, 2, N'WO', N'PART', N'OP', 11, 2,
                        N'Correction', N'operator', '2026-01-01T08:04:00+00:00');
            """, connection))
        {
            setup.Parameters.Add("@entry", System.Data.SqlDbType.UniqueIdentifier).Value = entry;
            setup.Parameters.Add("@revision", System.Data.SqlDbType.UniqueIdentifier).Value = revision;
            setup.Parameters.Add("@decision", System.Data.SqlDbType.NVarChar, 20).Value = decision;
            setup.Parameters.Add("@superseded", System.Data.SqlDbType.Bit).Value = superseded;
            await setup.ExecuteNonQueryAsync();
        }
        var writer = new SqlServerSupervisorQualificationWriter(database.ConnectionString);
        Assert.False(await writer.QualifyAsync(Guid.NewGuid(), revision, Guid.NewGuid(), "population", "test-authority"));
        Assert.Equal(expected, await writer.QualifyAsync(entry, revision, receipt, "population", "test-authority"));
        Assert.False(await writer.QualifyAsync(entry, revision, Guid.NewGuid(), "population", "test-authority"));
        await using var verify = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.SupervisorProductionQualificationReceipt
            WHERE ReceiptId = @receipt AND EvidenceId = @revision AND ProducedQuantity = 12
              AND GoodQuantity = 10 AND RejectedQuantity = 2 AND PartNumber = N'PART'
              AND PopulationKey = N'population' AND AuthorityReference = N'test-authority'
            """, connection);
        verify.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
        verify.Parameters.Add("@revision", System.Data.SqlDbType.UniqueIdentifier).Value = revision;
        Assert.Equal(expected ? 1L : 0L, (long)(await verify.ExecuteScalarAsync())!);
        var verifier = new SqlServerSupervisorVerificationWriter(database.ConnectionString);
        var verification = Guid.NewGuid();
        Assert.False(await verifier.VerifyAsync(receipt, Guid.NewGuid(), 12, "population", "test-authority"));
        Assert.False(await verifier.VerifyAsync(receipt, Guid.NewGuid(), 13, "population", "independent-authority"));
        Assert.False(await verifier.VerifyAsync(receipt, Guid.NewGuid(), 12, "wrong-population", "independent-authority"));
        Assert.False(await verifier.VerifyAsync(Guid.NewGuid(), Guid.NewGuid(), 12, "population", "independent-authority"));
        Assert.Equal(expected, await verifier.VerifyAsync(receipt, verification, 12, "population", "independent-authority"));
        Assert.False(await verifier.VerifyAsync(receipt, Guid.NewGuid(), 12, "population", "independent-authority"));
        await using (var check = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedProductionVerification
            WHERE VerificationId = @verification AND ReceiptId = @receipt
              AND VerifiedProducedQuantity = 12 AND Coverage = N'Complete'
              AND AuthorityReference = N'independent-authority'
            """, connection))
        {
            check.Parameters.Add("@verification", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            check.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            Assert.Equal(expected ? 1L : 0L, (long)(await check.ExecuteScalarAsync())!);
        }
        await using var count = new SqlCommand("""
            SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorProductionQualificationReceipt),
                   (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction),
                   (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedProductionVerification)
            """, connection);
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected ? 1L : 0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(expected ? 1L : 0L, reader.GetInt64(2));
    }
}
