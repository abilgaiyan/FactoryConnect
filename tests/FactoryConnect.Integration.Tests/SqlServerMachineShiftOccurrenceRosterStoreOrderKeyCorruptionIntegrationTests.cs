using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMachineShiftOccurrenceRosterStoreOrderKeyCorruptionIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMachineShiftOccurrenceRosterStoreOrderKeyCorruptionIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ParentOrderKeyMismatchIsCorruptionForReadAndReplacement()
    {
        var machineId = MachineId.New();
        var day = new ProductionDayId(
            new SiteId("E2-PARENT-KEY"),
            new DateOnly(2026, 9, 19));
        var line = new ProductionLineId("LINE-E2-PARENT-KEY");
        var store = new SqlServerMachineShiftOccurrenceRosterStore(_fixture.ConnectionString);
        var initial = new MachineShiftOccurrenceRoster(
            machineId,
            line,
            day,
            new MachineShiftOccurrenceRosterRevision(1),
            []);

        await store.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(null, initial),
            CancellationToken.None);

        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dbo.MachineShiftOccurrenceRoster
                SET ProductionDaySiteOrderKey = @CorruptedOrderKey
                WHERE MachineId = @MachineId
                  AND ProductionDaySiteId = @SiteId
                  AND ProductionBusinessDate = @BusinessDate;
                """;
            command.Parameters.Add(
                "@CorruptedOrderKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode("E2-PARENT-KEY-CORRUPTED");
            command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId.Value;
            command.Parameters.Add("@SiteId", SqlDbType.NVarChar, 256).Value = day.SiteId.Value;
            command.Parameters.Add("@BusinessDate", SqlDbType.Date).Value =
                day.BusinessDate.ToDateTime(TimeOnly.MinValue);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var readException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadAsync(machineId, day, CancellationToken.None));
        Assert.Contains("identity", readException.Message, StringComparison.OrdinalIgnoreCase);

        var replacement = new MachineShiftOccurrenceRoster(
            machineId,
            line,
            day,
            new MachineShiftOccurrenceRosterRevision(2),
            []);
        var commitException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(
                    new MachineShiftOccurrenceRosterRevision(1),
                    replacement),
                CancellationToken.None));
        Assert.Contains("identity", commitException.Message, StringComparison.OrdinalIgnoreCase);
    }
}
