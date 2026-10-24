using System.Text.Json.Nodes;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

// Separate opt-in operation: migration 021 only, never a runtime/bootstrap shortcut.
[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003PerformanceSchemaPreparationTests
{
    private const string Variable = "FACTORYCONNECT_GG003_PERF_CONNECTION_STRING";
    private const string Confirm = "FACTORYCONNECT_GG003_PERF_SCHEMA_CONFIRM";
    private const string LegacyProcessor = "operational-metrics:677b030b-1ede-4a67-82ae-6a494791357f:builtins-v1";

    [SchemaPreparationFact]
    public async Task ApplyExistingMigration021OnlyAndPreserveFreshPerformanceStartingState()
    {
        var connectionString = Environment.GetEnvironmentVariable(Variable)!;
        Gg003RealDataPerformanceTests.RequireTarget(connectionString);
        Assert.Equal("APPLY_EXISTING_021_ONLY", Environment.GetEnvironmentVariable(Confirm));
        // Prevent a later repository catalog from silently applying additional migrations.
        Assert.Equal(Enumerable.Range(1, 21), SqlMigrationCatalog.Load().Migrations.Select(m => m.MigrationId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        Assert.Equal(Gg003RealDataPerformanceTests.Database, (string)(await Scalar(connection, "SELECT DB_NAME();", ct))!);
        Assert.Equal(Enumerable.Range(1, 20), await History(connection, ct));
        Assert.Equal(DBNull.Value, await Scalar(connection,
            "SELECT OBJECT_ID(N'dbo.OperationalMetricConflictWitnessArchive', 'U');", ct));
        await VerifyLegacyOnly(connection, ct);
        var before = await ExistingRows(connection, ct);

        // This is the only write operation. The existing engine owns migration
        // locking, exact ledger/checksum validation, DDL transaction and final schema checks.
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), ct);

        Assert.Equal(Enumerable.Range(1, 21), await History(connection, ct));
        Assert.NotEqual(DBNull.Value, await Scalar(connection,
            "SELECT OBJECT_ID(N'dbo.OperationalMetricConflictWitnessArchive', 'U');", ct));
        await VerifyLegacyOnly(connection, ct);
        Assert.Equal(before, await ExistingRows(connection, ct));
        foreach (var table in new[] { "OperationalMetricConflictWitnessArchive", "OperationalMetricPublicationResultArchive",
                     "OperationalMetricResultWitnessReference" })
            Assert.Equal(0L, (long)(await Scalar(connection, $"SELECT COUNT_BIG(*) FROM dbo.{table};", ct))!);
        // No runtime, convergence coordinator, restore or containment publisher is invoked.
    }

    private static async Task VerifyLegacyOnly(SqlConnection connection, CancellationToken ct)
    {
        Assert.Equal(1L, (long)(await Scalar(connection,
            "SELECT COUNT_BIG(*) FROM dbo.OperationalMetricProjectionProcessor;", ct))!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Position FROM dbo.OperationalMetricProjectionCheckpoint c
            JOIN dbo.OperationalMetricProjectionProcessor p
              ON p.OperationalMetricProjectionProcessorRowId=c.OperationalMetricProjectionProcessorRowId
            WHERE p.ProcessorKey=@Legacy;
            """;
        command.Parameters.AddWithValue("@Legacy", LegacyProcessor);
        Assert.Equal(524m, (decimal)(await command.ExecuteScalarAsync(ct))!);
        Assert.Equal(1L, (long)(await Scalar(connection,
            "SELECT COUNT_BIG(*) FROM dbo.OperationalMetricProjectionCheckpoint;", ct))!);
    }

    private static async Task<int[]> History(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM dbo.FactoryConnectMigrationHistory ORDER BY MigrationId;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<int>();
        while (await reader.ReadAsync(ct)) ids.Add(Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        return ids.ToArray();
    }

    private static async Task<object?> Scalar(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<string> ExistingRows(SqlConnection connection, CancellationToken ct)
    {
        // Compare all pre-existing source/aggregation/reporting rows. Normalize only
        // the four columns introduced by migration 021, on the two altered tables.
        var tables = new (string Table, string Order)[]
        {
            ("MetricInputFact", "MetricInputFactRowId"),
            ("MetricAggregationCheckpoint", "MetricAggregationProcessorRowId"),
            ("MetricAggregationContribution", "MetricAggregationProcessorRowId,Position"),
            ("MetricAggregationRevision", "MetricAggregationProcessorRowId,Position"),
            ("MetricAggregationRevisionShiftOccurrence", "MetricAggregationProcessorRowId,Position,ShiftOccurrenceIdentityHash"),
            ("MetricAggregationRevisionProductionDay", "MetricAggregationProcessorRowId,Position,SiteOrderKey,ProductionBusinessDate"),
            ("OperationalMetricProjectionProcessor", "OperationalMetricProjectionProcessorRowId"),
            ("OperationalMetricProjectionCheckpoint", "OperationalMetricProjectionProcessorRowId"),
            ("OperationalMetricProjection", "OperationalMetricProjectionRowId"),
            ("OperationalMetricProjectionEvidence", "OperationalMetricProjectionEvidenceRowId"),
            ("OperationalMetricProjectionManifest", "OperationalMetricProjectionProcessorRowId,OperationalMetricProjectionRowId")
        };
        var states = new List<string>();
        foreach (var (table, order) in tables)
        {
            var json = (string)(await Scalar(connection,
                $"DECLARE @Snapshot nvarchar(max) = (SELECT * FROM dbo.{table} ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES); SELECT @Snapshot;", ct))!;
            var rows = JsonNode.Parse(json)!.AsArray();
            if (table is "OperationalMetricProjectionProcessor" or "OperationalMetricProjection")
                foreach (var row in rows)
                    foreach (var column in new[] { "PublicationFormatVersion", "EvaluationPolicyId", "ContainmentResultId", "ConflictWitnessId" })
                        row!.AsObject().Remove(column);
            states.Add(table + rows.ToJsonString());
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", states))));
    }

    public sealed class SchemaPreparationFactAttribute : FactAttribute
    {
        public SchemaPreparationFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)) ||
                Environment.GetEnvironmentVariable(Confirm) != "APPLY_EXISTING_021_ONLY")
                Skip = "Requires explicit migration-only consent for the exact fresh performance clone.";
        }
    }
}
