using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcceptedProductionWriterTests
{
    [Fact]
    public async Task WriterFailsClosedWithoutIndependentVerification()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        await using var command = new SqlCommand(
            "dbo.PublishSupervisorAcceptedProduction", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.Add("@PublicationId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        command.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        command.Parameters.Add("@VerificationId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(51038, error.Number);

        await using var verify = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction", connection);
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task VerifiedPublicationWritesQuantityAndAncestryAtomicallyAndRejectsReplay()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var receipt = Guid.NewGuid();
        var verification = Guid.NewGuid();
        var publication = Guid.NewGuid();
        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 ProductionOrder, PartNumber, Operation,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, @evidence, N'SupervisorRevision', N'population-one',
                    @machine, N'assignment-one', N'shift-1',
                    '2026-11-17T02:00:00+00:00', '2026-11-17T10:30:00+00:00',
                    N'WO-1', N'GEAR-1', N'OP-10',
                    N'pieces', 11, 10, 1, N'Qualified', N'test-only',
                    '2026-11-17T11:00:00+00:00');
            INSERT dbo.SupervisorAcceptedProductionVerification
                (VerificationId, ReceiptId, VerifiedProducedQuantity, Coverage,
                 AuthorityReference, VerifiedAtUtc)
            VALUES (@verification, @receipt, 11, N'Complete', N'test-only',
                    '2026-11-17T11:01:00+00:00');
            """, connection))
        {
            setup.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            setup.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@verification", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            await setup.ExecuteNonQueryAsync();
        }

        async Task ExecutePublicationAsync(Guid id)
        {
            await using var command = new SqlCommand("dbo.PublishSupervisorAcceptedProduction", connection)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            command.Parameters.Add("@PublicationId", System.Data.SqlDbType.UniqueIdentifier).Value = id;
            command.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            command.Parameters.Add("@VerificationId", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            await command.ExecuteNonQueryAsync();
        }

        await ExecutePublicationAsync(publication);
        await using (var verify = new SqlCommand("""
            SELECT COUNT_BIG(*)
            FROM dbo.SupervisorAcceptedShiftProduction AS p
            INNER JOIN dbo.SupervisorAcceptedShiftProductionSource AS s
                ON s.PublicationId = p.PublicationId
            WHERE p.PublicationId = @publication AND s.ReceiptId = @receipt
              AND p.AcceptedProducedQuantity = 11 AND p.Coverage = N'Complete'
              AND p.ProductionOrder = N'WO-1' AND p.PartNumber = N'GEAR-1'
              AND p.Operation = N'OP-10'
            """, connection))
        {
            verify.Parameters.Add("@publication", System.Data.SqlDbType.UniqueIdentifier).Value = publication;
            verify.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
        }

        var replay = await Assert.ThrowsAsync<SqlException>(
            () => ExecutePublicationAsync(Guid.NewGuid()));
        Assert.Equal(51039, replay.Number);

        await using var counts = new SqlCommand("""
            SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction),
                   (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource)
            """, connection);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task MismatchedVerifiedQuantityCannotPublishOrLeavePartialRows()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var receipt = Guid.NewGuid();
        var verification = Guid.NewGuid();
        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, @evidence, N'SupervisorRevision', N'population-two',
                    @machine, N'assignment-two', N'shift-2',
                    '2026-11-18T02:00:00+00:00', '2026-11-18T10:30:00+00:00',
                    N'pieces', 11, 10, 1, N'Qualified', N'test-only',
                    '2026-11-18T11:00:00+00:00');
            INSERT dbo.SupervisorAcceptedProductionVerification
                (VerificationId, ReceiptId, VerifiedProducedQuantity, Coverage,
                 AuthorityReference, VerifiedAtUtc)
            VALUES (@verification, @receipt, 12, N'Complete', N'test-only',
                    '2026-11-18T11:01:00+00:00');
            """, connection))
        {
            setup.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            setup.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@verification", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var command = new SqlCommand("dbo.PublishSupervisorAcceptedProduction", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        })
        {
            command.Parameters.Add("@PublicationId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            command.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            command.Parameters.Add("@VerificationId", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(51038, error.Number);
        }

        await using var counts = new SqlCommand("""
            SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction),
                   (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource)
            """, connection);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task ConcurrentPublicationOfSameReceiptProducesExactlyOnePublication()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var setupConnection = new SqlConnection(database.ConnectionString);
        await setupConnection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            setupConnection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var receipt = Guid.NewGuid();
        var verification = Guid.NewGuid();
        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, @evidence, N'SupervisorRevision', N'concurrent-population',
                    @machine, N'concurrent-assignment', N'shift-1',
                    '2026-11-19T02:00:00+00:00', '2026-11-19T10:30:00+00:00',
                    N'pieces', 11, 10, 1, N'Qualified', N'test-only',
                    '2026-11-19T11:00:00+00:00');
            INSERT dbo.SupervisorAcceptedProductionVerification
                (VerificationId, ReceiptId, VerifiedProducedQuantity, Coverage,
                 AuthorityReference, VerifiedAtUtc)
            VALUES (@verification, @receipt, 11, N'Complete', N'test-only',
                    '2026-11-19T11:01:00+00:00');
            """, setupConnection))
        {
            setup.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            setup.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@verification", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            await setup.ExecuteNonQueryAsync();
        }

        async Task<int> AttemptAsync()
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("dbo.PublishSupervisorAcceptedProduction", connection)
            {
                CommandType = System.Data.CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            command.Parameters.Add("@PublicationId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            command.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            command.Parameters.Add("@VerificationId", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            try
            {
                await command.ExecuteNonQueryAsync();
                return 0;
            }
            catch (SqlException error)
            {
                return error.Number;
            }
        }

        var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());
        Assert.Equal(1, results.Count(static result => result == 0));
        Assert.Equal(1, results.Count(static result => result == 51039));

        await using var counts = new SqlCommand("""
            SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction),
                   (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource)
            """, setupConnection);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task RejectedReceiptCannotPublishEvenWithMatchingVerificationRecord()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var receipt = Guid.NewGuid();
        var verification = Guid.NewGuid();
        await using (var setup = new SqlCommand("""
            INSERT dbo.SupervisorProductionQualificationReceipt
                (ReceiptId, EvidenceId, EvidenceKind, PopulationKey, MachineId,
                 ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                 QuantityUnit, ProducedQuantity, GoodQuantity, RejectedQuantity,
                 QualificationDecision, AuthorityReference, QualifiedAtUtc)
            VALUES (@receipt, @evidence, N'SupervisorRevision', N'rejected-population',
                    @machine, N'rejected-assignment', N'shift-1',
                    '2026-11-20T02:00:00+00:00', '2026-11-20T10:30:00+00:00',
                    N'pieces', 11, 10, 1, N'Rejected', N'test-only',
                    '2026-11-20T11:00:00+00:00');
            INSERT dbo.SupervisorAcceptedProductionVerification
                (VerificationId, ReceiptId, VerifiedProducedQuantity, Coverage,
                 AuthorityReference, VerifiedAtUtc)
            VALUES (@verification, @receipt, 11, N'Complete', N'test-only',
                    '2026-11-20T11:01:00+00:00');
            """, connection))
        {
            setup.Parameters.Add("@receipt", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            setup.Parameters.Add("@evidence", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@machine", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            setup.Parameters.Add("@verification", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var command = new SqlCommand("dbo.PublishSupervisorAcceptedProduction", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        })
        {
            command.Parameters.Add("@PublicationId", System.Data.SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            command.Parameters.Add("@ReceiptId", System.Data.SqlDbType.UniqueIdentifier).Value = receipt;
            command.Parameters.Add("@VerificationId", System.Data.SqlDbType.UniqueIdentifier).Value = verification;
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(51038, error.Number);
        }

        await using var counts = new SqlCommand("""
            SELECT (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProduction),
                   (SELECT COUNT_BIG(*) FROM dbo.SupervisorAcceptedShiftProductionSource)
            """, connection);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

}
