using System.Data;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricInputMachineBindingIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMetricInputMachineBindingIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MetricInputFactMachineMustMatchOwningStreamMachine()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var machineA = Guid.NewGuid();
        var machineB = Guid.NewGuid();
        var streamRowId = await InsertStreamAsync(
            connection,
            machineA,
            "metric-inputs-machine-binding");

        var acceptedFactRowId = await InsertFactAsync(
            connection,
            streamRowId,
            machineA,
            1m,
            "FACT-MACHINE-A");

        Assert.True(acceptedFactRowId > 0);

        await Assert.ThrowsAsync<SqlException>(() =>
            InsertFactAsync(
                connection,
                streamRowId,
                machineB,
                2m,
                "FACT-MACHINE-B"));
    }

    private static async Task<long> InsertStreamAsync(
        SqlConnection connection,
        Guid machineId,
        string streamKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.MetricInputStream " +
            "(MachineId, StreamKeyBinary, StreamKey) " +
            "OUTPUT INSERTED.MetricInputStreamRowId " +
            "VALUES (@MachineId, @StreamKeyBinary, @StreamKey);";
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(streamKey);
        command.Parameters.Add("@StreamKey", SqlDbType.NVarChar, 256).Value = streamKey;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertFactAsync(
        SqlConnection connection,
        long streamRowId,
        Guid machineId,
        decimal position,
        string factId)
    {
        var occurrenceStart =
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var occurrenceEnd = occurrenceStart.AddHours(8);
        var factStart = occurrenceStart.AddHours(1);
        var factEnd = factStart.AddMinutes(1);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.MetricInputFact " +
            "(MetricInputStreamRowId, Position, FactIdBinary, FactId, MetricInputKey, " +
            "MetricValue, Unit, StartsAtUtc, EndsAtUtc, CompanyId, SiteId, " +
            "ProductionLineId, MachineId, ShiftId, ShiftScheduleAssignmentId, " +
            "OccurrenceSiteId, OccurrenceShiftScheduleAssignmentId, OccurrenceShiftId, " +
            "OccurrenceStartsAtUtc, OccurrenceEndsAtUtc, ProductionDaySiteId, " +
            "ProductionBusinessDate) " +
            "OUTPUT INSERTED.MetricInputFactRowId " +
            "VALUES (@StreamRowId, @Position, @FactIdBinary, @FactId, @MetricInputKey, " +
            "@MetricValue, @Unit, @StartsAtUtc, @EndsAtUtc, @CompanyId, @SiteId, " +
            "@ProductionLineId, @MachineId, @ShiftId, @ScheduleId, @OccurrenceSiteId, " +
            "@ScheduleId, @ShiftId, @OccurrenceStartsAtUtc, @OccurrenceEndsAtUtc, " +
            "@ProductionDaySiteId, @ProductionBusinessDate);";
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add("@Position", SqlDbType.Decimal).Value = position;
        command.Parameters.Add("@FactIdBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(factId);
        command.Parameters.Add("@FactId", SqlDbType.NVarChar, 256).Value = factId;
        command.Parameters.Add("@MetricInputKey", SqlDbType.NVarChar, 256).Value =
            "running-duration";
        command.Parameters.Add("@MetricValue", SqlDbType.NVarChar, 64).Value = "1";
        command.Parameters.Add("@Unit", SqlDbType.NVarChar, 128).Value = "seconds";
        command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value = factStart;
        command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value = factEnd;
        command.Parameters.Add("@CompanyId", SqlDbType.NVarChar, 256).Value = "COMP-1";
        command.Parameters.Add("@SiteId", SqlDbType.NVarChar, 256).Value = "SITE-1";
        command.Parameters.Add("@ProductionLineId", SqlDbType.NVarChar, 256).Value = "LINE-1";
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add("@ShiftId", SqlDbType.NVarChar, 256).Value = "SHIFT-A";
        command.Parameters.Add("@ScheduleId", SqlDbType.NVarChar, 256).Value = "SCHEDULE-A";
        command.Parameters.Add("@OccurrenceSiteId", SqlDbType.NVarChar, 256).Value = "SITE-1";
        command.Parameters.Add("@OccurrenceStartsAtUtc", SqlDbType.DateTimeOffset).Value = occurrenceStart;
        command.Parameters.Add("@OccurrenceEndsAtUtc", SqlDbType.DateTimeOffset).Value = occurrenceEnd;
        command.Parameters.Add("@ProductionDaySiteId", SqlDbType.NVarChar, 256).Value = "SITE-1";
        command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
            new DateTime(2026, 8, 27);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
