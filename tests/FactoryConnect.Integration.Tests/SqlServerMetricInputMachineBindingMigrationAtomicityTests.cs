using System.Data;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricInputMachineBindingMigrationAtomicityTests
{
    [Fact]
    public async Task FailedMachineBindingMigrationRollsBackAllSchemaChanges()
    {
        var sourceConnectionString = Environment.GetEnvironmentVariable(
            SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(sourceConnectionString));

        var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
        var databaseName = $"FactoryConnect_FC026_Migration_{Guid.NewGuid():N}";
        var adminBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            InitialCatalog = "master",
        };
        var databaseBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            InitialCatalog = databaseName,
        };

        try
        {
            await CreateDatabaseAsync(adminBuilder.ConnectionString, databaseName);

            await using var connection = new SqlConnection(databaseBuilder.ConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, SqlServerSchema.ReadInitialSchema());
            await ExecuteAsync(connection, SqlServerSchema.ReadMetricAggregationSchema());

            var machineA = Guid.NewGuid();
            var machineB = Guid.NewGuid();
            var streamRowId = await InsertStreamAsync(connection, machineA);
            await InsertFactAsync(connection, streamRowId, machineB);

            await Assert.ThrowsAsync<SqlException>(() =>
                ExecuteAsync(
                    connection,
                    SqlServerSchema.ReadMetricInputMachineBindingSchema()));

            Assert.True(await ConstraintExistsAsync(
                connection,
                "MetricInputFact",
                "FK_MetricInputFact_MetricInputStream"));
            Assert.False(await ConstraintExistsAsync(
                connection,
                "MetricInputStream",
                "UQ_MetricInputStream_RowMachine"));
            Assert.False(await ConstraintExistsAsync(
                connection,
                "MetricInputFact",
                "FK_MetricInputFact_StreamMachine"));
        }
        finally
        {
            await DropDatabaseAsync(adminBuilder.ConnectionString, databaseName);
        }
    }

    private static async Task CreateDatabaseAsync(
        string adminConnectionString,
        string databaseName)
    {
        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{EscapeIdentifier(databaseName)}]";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(
        string adminConnectionString,
        string databaseName)
    {
        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var escapedName = EscapeIdentifier(databaseName);
        command.CommandText =
            $"IF DB_ID(N'{EscapeLiteral(databaseName)}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{escapedName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{escapedName}]; END";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertStreamAsync(
        SqlConnection connection,
        Guid machineId)
    {
        const string streamKey = "metric-inputs-migration-atomicity";
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

    private static async Task InsertFactAsync(
        SqlConnection connection,
        long streamRowId,
        Guid machineId)
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
            "VALUES (@StreamRowId, 1, @FactIdBinary, @FactId, @MetricInputKey, " +
            "@MetricValue, @Unit, @StartsAtUtc, @EndsAtUtc, @CompanyId, @SiteId, " +
            "@ProductionLineId, @MachineId, @ShiftId, @ScheduleId, @OccurrenceSiteId, " +
            "@ScheduleId, @ShiftId, @OccurrenceStartsAtUtc, @OccurrenceEndsAtUtc, " +
            "@ProductionDaySiteId, @ProductionBusinessDate);";
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add("@FactIdBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode("FACT-MIGRATION-MISMATCH");
        command.Parameters.Add("@FactId", SqlDbType.NVarChar, 256).Value =
            "FACT-MIGRATION-MISMATCH";
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
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ConstraintExistsAsync(
        SqlConnection connection,
        string tableName,
        string constraintName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.objects " +
            "WHERE parent_object_id = OBJECT_ID(@TableName) " +
            "AND name = @ConstraintName " +
            "AND type IN ('F', 'UQ');";
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value =
            $"dbo.{tableName}";
        command.Parameters.Add("@ConstraintName", SqlDbType.NVarChar, 128).Value = constraintName;
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        return count == 1;
    }

    private static string EscapeIdentifier(string value) =>
        value.Replace("]", "]]", StringComparison.Ordinal);

    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
