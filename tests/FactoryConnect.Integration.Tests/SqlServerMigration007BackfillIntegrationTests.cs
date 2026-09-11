using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration007BackfillIntegrationTests
{
    private static readonly int[] MigrationIdsThrough006 = [1, 2, 3, 4, 5, 6];
    private static readonly int[] MigrationIdsThroughCurrent = [1, 2, 3, 4, 5, 6, 7];
    private static readonly ulong[] FirstStreamPositions = [1UL, 2UL, 3UL];
    private static readonly ulong[] SecondStreamPositions = [1UL, 2UL];

    [Fact]
    public async Task ExactPost006BackfillsDeterministicPerStreamPositionsWithoutFabricatingAuthority()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();

        await CreateExactPrefixAsync(connection, catalog, prefixLength: 6);
        await SeedLegacyObservationDataAsync(connection);

        Assert.Equal(MigrationIdsThrough006, await ReadMigrationIdsAsync(connection));
        Assert.False(await ColumnExistsAsync(connection, "dbo", "MachineObservation", "Position"));
        Assert.False(await TableExistsAsync(connection, "dbo", "AcquisitionContactAuthority"));

        var engine = new SqlServerMigrationEngine(catalog, new FixedUtcClock());
        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(MigrationIdsThroughCurrent, await ReadMigrationIdsAsync(connection));
        Assert.Equal(
            FirstStreamPositions,
            await ReadPositionsAsync(connection, 0x01));
        Assert.Equal(
            SecondStreamPositions,
            await ReadPositionsAsync(connection, 0x02));
        Assert.Equal(0, await ReadAuthorityCountAsync(connection));

        var verifier = new SqlServerRuntimeSchemaCompatibilityVerifier(catalog);
        var outcome = await verifier.VerifyAsync(connection, CancellationToken.None);
        Assert.Equal(SqlRuntimeSchemaCompatibilityStatus.Compatible, outcome.Status);
    }

    private static async Task CreateExactPrefixAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog,
        int prefixLength)
    {
        Assert.InRange(prefixLength, 0, catalog.Migrations.Length);
        await using var transaction = connection.BeginTransaction();
        await SqlServerMigrationLedgerCreator.CreateAsync(
            connection,
            transaction,
            CancellationToken.None);
        var historyStore = new SqlServerMigrationHistoryStore(new FixedUtcClock());

        for (var index = 0; index < prefixLength; index++)
        {
            var migration = catalog.Migrations[index];
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None);
            await historyStore.InsertAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None);
        }

        await transaction.CommitAsync();
    }

    private static async Task SeedLegacyObservationDataAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @MachineId uniqueidentifier = '00112233-4455-6677-8899-AABBCCDDEEFF';

            INSERT INTO dbo.ObservationStreamCheckpoint
                (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
            VALUES
                (@MachineId, 0x01, N'legacy-a', 2, 21),
                (@MachineId, 0x02, N'legacy-b', 5, 201);

            INSERT INTO dbo.MachineObservation
            (
                MachineId,
                StreamKeyBinary,
                InstanceId,
                Sequence,
                Source,
                Address,
                SignalType,
                ObservationValue,
                Quality,
                ObservedAt
            )
            VALUES
                (@MachineId, 0x01, 2, 20, N'MTConnect', N'a-3', 3, N'ACTIVE', 0, '2026-09-11T08:20:00+00:00'),
                (@MachineId, 0x01, 1, 30, N'MTConnect', N'a-2', 3, N'ACTIVE', 0, '2026-09-11T08:30:00+00:00'),
                (@MachineId, 0x01, 1, 10, N'MTConnect', N'a-1', 3, N'ACTIVE', 0, '2026-09-11T08:10:00+00:00'),
                (@MachineId, 0x02, 5, 200, N'MTConnect', N'b-2', 3, N'ACTIVE', 0, '2026-09-11T08:20:00+00:00'),
                (@MachineId, 0x02, 5, 100, N'MTConnect', N'b-1', 3, N'ACTIVE', 0, '2026-09-11T08:10:00+00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ulong[]> ReadPositionsAsync(
        SqlConnection connection,
        byte streamKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Position
            FROM dbo.MachineObservation
            WHERE MachineId = '00112233-4455-6677-8899-AABBCCDDEEFF'
              AND StreamKeyBinary = @StreamKeyBinary
            ORDER BY InstanceId, Sequence;
            """;
        command.Parameters.AddWithValue(
            "@StreamKeyBinary",
            new byte[] { streamKey });

        var positions = new List<ulong>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            positions.Add(SqlServerUInt64.Materialize(reader.GetDecimal(0)));
        }

        return positions.ToArray();
    }

    private static async Task<int[]> ReadMigrationIdsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MigrationId
            FROM dbo.FactoryConnectMigrationHistory
            ORDER BY MigrationId;
            """;

        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids.ToArray();
    }

    private static async Task<int> ReadAuthorityCountAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.AcquisitionContactAuthority;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string schema,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN OBJECT_ID(QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName), N'U') IS NULL
                THEN 0 ELSE 1 END;
            """;
        command.Parameters.AddWithValue("@SchemaName", schema);
        command.Parameters.AddWithValue("@TableName", table);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string schema,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM sys.columns AS c
                INNER JOIN sys.tables AS t ON t.object_id = c.object_id
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE s.name = @SchemaName
                  AND t.name = @TableName
                  AND c.name = @ColumnName
            ) THEN 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("@SchemaName", schema);
        command.Parameters.AddWithValue("@TableName", table);
        command.Parameters.AddWithValue("@ColumnName", column);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public DateTimeOffset GetUtcNow() =>
            new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    }
}
