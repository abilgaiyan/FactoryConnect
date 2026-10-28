using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class CommissioningProcessorSqlTests
{
    [Fact]
    public async Task ReadsExactOwnershipWithoutBindingOrAdvancingAndRejectsOversizedInventory()
    {
        await using var fixture = new FixtureLifetime();
        await fixture.Database.InitializeAsync();
        await using var connection = fixture.Database.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.ProductionContextProcessor
                (ProcessorKeyHash,ProcessorKeyBinary,ProcessorKey,MachineId,
                 ObservationStreamKeyHash,ObservationStreamKeyBinary,ObservationStreamKey,AdmissionBound,ProductionStartsAt)
            VALUES (0x01,0x01,N'activity-gg003','11111111-1111-1111-1111-111111111111',0x01,0x01,N'gg003',0,NULL),
                   (0x02,0x02,N'activity-moxa','22222222-2222-2222-2222-222222222222',0x02,0x02,N'moxa',1,'2026-10-25T02:00:00Z');
            INSERT dbo.ProductionContextCheckpoint (ProductionContextProcessorRowId,Position)
            SELECT ProductionContextProcessorRowId,18446744073709551615
            FROM dbo.ProductionContextProcessor WHERE ProcessorKey=N'activity-moxa';
            """;
        await command.ExecuteNonQueryAsync();
        var rows = await CommissioningProcessorReader.ReadAsync(fixture.Database.ConnectionString, CancellationToken.None);
        Assert.Equal(2, rows.Length);
        Assert.Equal("gg003", rows[0].StreamKey);
        Assert.Null(rows[0].Position);
        Assert.False(rows[0].AdmissionBound);
        Assert.Equal("moxa", rows[1].StreamKey);
        Assert.Equal("18446744073709551615", rows[1].Position);
        Assert.True(rows[1].AdmissionBound);
        command.CommandText = "SELECT COUNT(*) FROM dbo.ProductionContextProcessor WHERE AdmissionBound=0;";
        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            DECLARE @i int=0;
            WHILE @i<127 BEGIN
              DECLARE @key nvarchar(256)=CONCAT(N'limit-',@i);
              INSERT dbo.ProductionContextProcessor
                (ProcessorKeyHash,ProcessorKeyBinary,ProcessorKey,MachineId,ObservationStreamKeyHash,ObservationStreamKeyBinary,ObservationStreamKey)
              VALUES (HASHBYTES('SHA2_256',@key),CONVERT(varbinary(512),@key),@key,NEWID(),0x03,0x03,N'limit');
              SET @i+=1;
            END;
            """;
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommissioningProcessorReader.ReadAsync(fixture.Database.ConnectionString, CancellationToken.None));
    }

    private sealed class FixtureLifetime : IAsyncDisposable
    {
        public SqlServerTestDatabaseFixture Database { get; } = new();
        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }
}
