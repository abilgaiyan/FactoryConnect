using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricDecimalRoundTripIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMetricDecimalRoundTripIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("1E-28")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-123456789.125")]
    public async Task MetricInputDecimalRoundTripsThroughSql(string canonicalValue)
    {
        var expected = SqlServerCanonicalDecimalCodec.Deserialize(canonicalValue);
        var machineId = new MachineId(Guid.NewGuid());
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var positioned = await store.AppendAsync(
            CreateAppend(machineId, expected),
            CancellationToken.None);

        var batch = await store.ReadAsync(
            new MetricInputReadRequest(
                positioned.StreamId,
                afterPosition: null,
                maxCount: 1),
            CancellationToken.None);

        var fact = Assert.Single(batch.Facts);
        Assert.Equal(expected, fact.Fact.Value);
        Assert.Equal(
            SqlServerCanonicalDecimalCodec.Serialize(expected),
            SqlServerCanonicalDecimalCodec.Serialize(fact.Fact.Value));
    }

    [Fact]
    public async Task MalformedPersistedMetricDecimalIsRejected()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var positioned = await store.AppendAsync(
            CreateAppend(machineId, 1m),
            CancellationToken.None);

        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE f SET MetricValue = @MetricValue " +
                "FROM dbo.MetricInputFact f " +
                "JOIN dbo.MetricInputStream s " +
                "ON s.MetricInputStreamRowId = f.MetricInputStreamRowId " +
                "WHERE s.MachineId = @MachineId AND f.Position = @Position;";
            command.Parameters.Add("@MetricValue", SqlDbType.NVarChar, 64).Value =
                "not-a-decimal";
            command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
                machineId.Value;
            command.Parameters.Add(SqlServerUInt64.CreateParameter(
                "@Position",
                positioned.Position.Value));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ReadAsync(
                new MetricInputReadRequest(
                    positioned.StreamId,
                    afterPosition: null,
                    maxCount: 1),
                CancellationToken.None));
    }

    private static DurableMetricInputAppend CreateAppend(
        MachineId machineId,
        decimal value)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart =
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId($"decimal-{Guid.NewGuid():N}"),
            Key = "running-duration",
            Value = value,
            Unit = "seconds",
            StartsAtUtc = occurrenceStart.AddMinutes(1),
            EndsAtUtc = occurrenceStart.AddMinutes(2),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            ShiftId = shiftId,
            ShiftScheduleAssignmentId = scheduleId,
        };

        return new DurableMetricInputAppend(
            MetricInputStreamId.ForMachine(machineId),
            fact,
            new ShiftOccurrenceId(
                siteId,
                scheduleId,
                shiftId,
                occurrenceStart,
                occurrenceStart.AddHours(8)),
            new ProductionDayId(siteId, new DateOnly(2026, 8, 27)));
    }
}
