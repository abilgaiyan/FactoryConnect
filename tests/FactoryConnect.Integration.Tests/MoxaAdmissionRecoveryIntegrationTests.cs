using System.Security.Cryptography;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class MoxaAdmissionRecoveryIntegrationTests
{
    [Fact]
    public async Task CorrectionAuditsAtomicallyAndLeavesQuantityUntouchedAndRejectsReplay()
    {
        await using var db = await CreateAsync();
        var target = await IdentityAsync(db.ConnectionString);
        var operation = Guid.NewGuid();
        await RunAsync(db.ConnectionString, operation, target);
        Assert.Equal(1L, await ScalarAsync(db.ConnectionString, "SELECT COUNT_BIG(*) FROM dbo.ProductionAdmissionRecoveryAudit WHERE SchemaVersion=1 AND BeforeBoundary IS NULL AND AfterBoundary='2026-10-25T02:00:00+00:00';"));
        Assert.Equal(1L, await ScalarAsync(db.ConnectionString, "SELECT COUNT_BIG(*) FROM dbo.ProductionContextProcessor WHERE ProductionContextProcessorRowId=2 AND AdmissionBound=1 AND ProductionStartsAt='2026-10-25T02:00:00+00:00';"));
        Assert.Equal(1L, await ScalarAsync(db.ConnectionString, "SELECT COUNT_BIG(*) FROM dbo.ProductionContextProcessor WHERE ProductionContextProcessorRowId=4 AND AdmissionBound=1 AND ProductionStartsAt IS NULL;"));
        Assert.Equal(0L, await ScalarAsync(db.ConnectionString, "SELECT COUNT_BIG(*) FROM dbo.ProductionContextCheckpoint;"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(db.ConnectionString, operation, target));
    }

    [Fact]
    public async Task FailureAfterAuditRollsBackBothAuditAndCorrectionAndAllowsRetry()
    {
        await using var db = await CreateAsync();
        var target = await IdentityAsync(db.ConnectionString);
        var operation = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(db.ConnectionString, operation, target,
            () => throw new InvalidOperationException("Injected after audit")));
        await AssertUnchangedAsync(db.ConnectionString);
        await RunAsync(db.ConnectionString, operation, target);
    }

    [Theory]
    [InlineData("INSERT dbo.ProductionContextCheckpoint VALUES (2,1);")]
    [InlineData("UPDATE dbo.ProductionContextProcessor SET AdmissionBound=0 WHERE ProductionContextProcessorRowId=2;")]
    [InlineData("UPDATE dbo.ProductionContextProcessor SET ProductionStartsAt='2026-10-24T00:00:00+00:00' WHERE ProductionContextProcessorRowId=2;")]
    [InlineData("UPDATE dbo.ProductionContextProcessor SET ProcessorKeyBinary=0x01 WHERE ProductionContextProcessorRowId=2;")]
    [InlineData("INSERT dbo.MetricInputStream (MachineId,StreamKeyBinary,StreamKey) VALUES ('bfa0dc97-9a68-45ed-b189-1dacb7d863f0',0x01,N'other');")]
    [InlineData("INSERT dbo.ContextualizedActivityOutput (IdentityHash,IdentityBinary,IdentityText,PayloadHash,Payload) VALUES (0x01,0x01,N'opaque',0x01,N'{}');")]
    [InlineData("INSERT dbo.ProductionTimeEligibilityOutput (IdentityHash,IdentityBinary,IdentityText,PayloadHash,Payload) VALUES (0x01,0x01,N'opaque',0x01,N'{}');")]
    public async Task ChangedPreconditionsAbortWithoutAudit(string mutation)
    {
        await using var db = await CreateAsync();
        await ExecuteAsync(db.ConnectionString, mutation);
        var target = await IdentityAsync(db.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(db.ConnectionString, Guid.NewGuid(), target));
        Assert.Equal(0L, await ScalarAsync(db.ConnectionString, "SELECT COUNT_BIG(*) FROM dbo.ProductionAdmissionRecoveryAudit;"));
    }

    [Fact]
    public async Task WrongDatabaseIdentityAborts()
    {
        await using var db = await CreateAsync();
        var target = await IdentityAsync(db.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(db.ConnectionString, Guid.NewGuid(), (target.Server,"wrong-database")));
        await AssertUnchangedAsync(db.ConnectionString);
    }

    [Fact]
    public async Task ProcessorAndCheckpointAbsenceStayProtectedUntilCommit()
    {
        await using var db = await CreateAsync();
        var target = await IdentityAsync(db.ConnectionString);
        await RunAsync(db.ConnectionString, Guid.NewGuid(), target, async () =>
        {
            await using var competing = new SqlConnection(db.ConnectionString);
            await competing.OpenAsync();
            using var command = competing.CreateCommand();
            command.CommandText = "SET LOCK_TIMEOUT 100; INSERT dbo.ProductionContextCheckpoint VALUES (2,1);";
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(1222, error.Number);
        });
    }

    private static async Task<SqlStartupIsolatedDatabase> CreateAsync()
    {
        var db = await SqlStartupIsolatedDatabase.CreateAsync();
        try
        {
            await using var connection = new SqlConnection(db.ConnectionString);
            await connection.OpenAsync();
            await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SET IDENTITY_INSERT dbo.ProductionContextProcessor ON;
                INSERT dbo.ProductionContextProcessor
                  (ProductionContextProcessorRowId,ProcessorKeyHash,ProcessorKeyBinary,ProcessorKey,MachineId,
                   ObservationStreamKeyHash,ObservationStreamKeyBinary,ObservationStreamKey,AdmissionBound)
                VALUES (2,@PH,@PB,@P,@M,@SH,@SB,@S,1),
                  (4,0x04,0x04,N'production-quantity:bfa0dc97-9a68-45ed-b189-1dacb7d863f0',@M,0x04,0x04,N'production-quantity',1);
                SET IDENTITY_INSERT dbo.ProductionContextProcessor OFF;
                """;
            var pb = OrdinalStringKeyCodec.Encode(MoxaAdmissionRecovery.Processor);
            var sb = OrdinalStringKeyCodec.Encode(MoxaAdmissionRecovery.Stream);
            command.Parameters.AddWithValue("@PH", SHA256.HashData(pb));
            command.Parameters.AddWithValue("@PB", pb); command.Parameters.AddWithValue("@P", MoxaAdmissionRecovery.Processor);
            command.Parameters.AddWithValue("@M", MoxaAdmissionRecovery.Machine);
            command.Parameters.AddWithValue("@SH", SHA256.HashData(sb));
            command.Parameters.AddWithValue("@SB", sb); command.Parameters.AddWithValue("@S", MoxaAdmissionRecovery.Stream);
            await command.ExecuteNonQueryAsync();
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private static Task RunAsync(string cs, Guid operation, (string Server,string Database) target, Func<Task>? seam = null) =>
        MoxaAdmissionRecovery.ExecuteCoreAsync(cs, operation, target.Server, target.Database, "test approval", "test maintenance ownership", seam, CancellationToken.None);

    private static async Task<(string Server,string Database)> IdentityAsync(string cs)
    {
        await using var connection = new SqlConnection(cs); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT CONVERT(nvarchar(256),SERVERPROPERTY('ServerName')),DB_NAME();";
        await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
        return (reader.GetString(0),reader.GetString(1));
    }
    private static async Task<long> ScalarAsync(string cs,string sql)
    {
        await using var connection = new SqlConnection(cs); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private static async Task ExecuteAsync(string cs,string sql)
    {
        await using var connection = new SqlConnection(cs); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task AssertUnchangedAsync(string cs)
    {
        Assert.Equal(0L, await ScalarAsync(cs,"SELECT COUNT_BIG(*) FROM dbo.ProductionAdmissionRecoveryAudit;"));
        Assert.Equal(1L, await ScalarAsync(cs,"SELECT COUNT_BIG(*) FROM dbo.ProductionContextProcessor WHERE ProductionContextProcessorRowId=2 AND ProductionStartsAt IS NULL;"));
    }
}
