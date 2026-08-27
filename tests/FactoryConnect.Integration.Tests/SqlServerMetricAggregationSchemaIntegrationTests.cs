using System.Data;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricAggregationSchemaIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly decimal UInt64Maximum =
        18446744073709551615m;

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMetricAggregationSchemaIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MetricAggregationMigrationCreatesTablesAndSqlSafeAggregateIdentityIndexes()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sys.tables WHERE name IN " +
            "('MetricInputStream', 'MetricInputFact', 'MetricAggregationProcessor', " +
            "'MetricAggregationCheckpoint', 'MetricAggregationContribution', " +
            "'ShiftMetricAggregate', 'ProductionDayMetricAggregate');";
        var tableCount = Convert.ToInt32(
            await tableCommand.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(7, tableCount);

        Assert.Equal(
            "ShiftMetricAggregateRowId",
            await ReadPrimaryKeyColumnAsync(connection, "ShiftMetricAggregate"));
        Assert.Equal(
            "ProductionDayMetricAggregateRowId",
            await ReadPrimaryKeyColumnAsync(connection, "ProductionDayMetricAggregate"));

        Assert.True(await UniqueIndexExistsAsync(
            connection,
            "ShiftMetricAggregate",
            "UQ_ShiftMetricAggregate_IdentityHash"));
        Assert.True(await UniqueIndexExistsAsync(
            connection,
            "ProductionDayMetricAggregate",
            "UQ_ProductionDayMetricAggregate_IdentityHash"));
    }

    [Fact]
    public async Task MetricInputFactAcceptsMaximumUInt64PositionAndValidOwnership()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var machineId = Guid.NewGuid();
        var streamRowId = await InsertStreamAsync(
            connection,
            machineId,
            "metric-inputs-max");

        var factRowId = await InsertFactAsync(
            connection,
            streamRowId,
            machineId,
            UInt64Maximum,
            "FACT-MAX");

        Assert.True(factRowId > 0);
    }

    [Fact]
    public async Task ContributionRejectsCrossStreamFactAndPositionMismatch()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var machineA = Guid.NewGuid();
        var machineB = Guid.NewGuid();
        var streamA = await InsertStreamAsync(connection, machineA, "metric-inputs-a");
        var streamB = await InsertStreamAsync(connection, machineB, "metric-inputs-b");
        var processorA = await InsertProcessorAsync(connection, streamA, "aggregate-a");
        var factA = await InsertFactAsync(connection, streamA, machineA, 1m, "FACT-A");
        var factB = await InsertFactAsync(connection, streamB, machineB, 1m, "FACT-B");

        await Assert.ThrowsAsync<SqlException>(() =>
            InsertContributionAsync(
                connection,
                processorA,
                streamA,
                factB,
                1m));

        await Assert.ThrowsAsync<SqlException>(() =>
            InsertContributionAsync(
                connection,
                processorA,
                streamA,
                factA,
                2m));
    }

    [Fact]
    public async Task MetricInputFactRejectsContradictoryOwnershipAndNonUtcOccurrenceIdentity()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var machineId = Guid.NewGuid();
        var streamRowId = await InsertStreamAsync(
            connection,
            machineId,
            "metric-inputs-validation");

        await Assert.ThrowsAsync<SqlException>(() =>
            InsertFactAsync(
                connection,
                streamRowId,
                machineId,
                1m,
                "FACT-SITE-MISMATCH",
                occurrenceSiteId: "SITE-2"));

        var offset = TimeSpan.FromHours(5.5);
        await Assert.ThrowsAsync<SqlException>(() =>
            InsertFactAsync(
                connection,
                streamRowId,
                machineId,
                2m,
                "FACT-NON-UTC",
                occurrenceStartsAt: new DateTimeOffset(2026, 8, 27, 6, 0, 0, offset),
                occurrenceEndsAt: new DateTimeOffset(2026, 8, 27, 14, 0, 0, offset),
                startsAt: new DateTimeOffset(2026, 8, 27, 7, 0, 0, offset),
                endsAt: new DateTimeOffset(2026, 8, 27, 7, 1, 0, offset)));
    }

    private static async Task<string?> ReadPrimaryKeyColumnAsync(
        SqlConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COL_NAME(ic.object_id, ic.column_id) " +
            "FROM sys.indexes i " +
            "JOIN sys.index_columns ic " +
            "ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
            "WHERE i.is_primary_key = 1 AND OBJECT_NAME(i.object_id) = @TableName " +
            "ORDER BY ic.key_ordinal;";
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = tableName;
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<bool> UniqueIndexExistsAsync(
        SqlConnection connection,
        string tableName,
        string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.indexes " +
            "WHERE object_id = OBJECT_ID(@TableName) " +
            "AND name = @IndexName AND is_unique = 1;";
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value =
            $"dbo.{tableName}";
        command.Parameters.Add("@IndexName", SqlDbType.NVarChar, 128).Value = indexName;
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        return count == 1;
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

    private static async Task<long> InsertProcessorAsync(
        SqlConnection connection,
        long streamRowId,
        string processorKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.MetricAggregationProcessor " +
            "(ProcessorKeyBinary, ProcessorKey, MetricInputStreamRowId) " +
            "OUTPUT INSERTED.MetricAggregationProcessorRowId " +
            "VALUES (@ProcessorKeyBinary, @ProcessorKey, @MetricInputStreamRowId);";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(processorKey);
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorKey;
        command.Parameters.Add("@MetricInputStreamRowId", SqlDbType.BigInt).Value = streamRowId;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertFactAsync(
        SqlConnection connection,
        long streamRowId,
        Guid machineId,
        decimal position,
        string factId,
        string occurrenceSiteId = "SITE-1",
        DateTimeOffset? occurrenceStartsAt = null,
        DateTimeOffset? occurrenceEndsAt = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null)
    {
        var occurrenceStart = occurrenceStartsAt ??
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var occurrenceEnd = occurrenceEndsAt ??
            new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero);
        var factStart = startsAt ?? occurrenceStart.AddHours(1);
        var factEnd = endsAt ?? factStart.AddMinutes(1);

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
        command.Parameters.Add("@OccurrenceSiteId", SqlDbType.NVarChar, 256).Value = occurrenceSiteId;
        command.Parameters.Add("@OccurrenceStartsAtUtc", SqlDbType.DateTimeOffset).Value = occurrenceStart;
        command.Parameters.Add("@OccurrenceEndsAtUtc", SqlDbType.DateTimeOffset).Value = occurrenceEnd;
        command.Parameters.Add("@ProductionDaySiteId", SqlDbType.NVarChar, 256).Value = "SITE-1";
        command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
            new DateTime(2026, 8, 27);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertContributionAsync(
        SqlConnection connection,
        long processorRowId,
        long streamRowId,
        long factRowId,
        decimal position)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.MetricAggregationContribution " +
            "(MetricAggregationProcessorRowId, MetricInputStreamRowId, " +
            "MetricInputFactRowId, Position) " +
            "VALUES (@ProcessorRowId, @StreamRowId, @FactRowId, @Position);";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add("@FactRowId", SqlDbType.BigInt).Value = factRowId;
        command.Parameters.Add("@Position", SqlDbType.Decimal).Value = position;
        await command.ExecuteNonQueryAsync();
    }
}
