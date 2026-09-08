using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration006ConformanceIntegrationTests
{
    private const string OldForeignKeyName =
        "FK_OperationalMetricProjectionManifest_Checkpoint";
    private const string NewForeignKeyName =
        "FK_OperationalMetricProjectionManifest_Processor";

    private static readonly int[] MigrationIdsThrough005 = [1, 2, 3, 4, 5];
    private static readonly int[] MigrationIdsThrough006 = [1, 2, 3, 4, 5, 6];

    [Fact]
    public async Task ExactPost005Applies006WithTrustedParentFkAndPreservesExistingPublicationData()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(connection, catalog, prefixLength: 5);
        await SeedPublicationDataAsync(connection);

        var before = await ReadPublicationSnapshotAsync(connection);
        Assert.Equal(MigrationIdsThrough005, await ReadMigrationIdsAsync(connection));
        await AssertPost005ForeignKeyStateAsync(connection);

        var engine = new SqlServerMigrationEngine(catalog, new FixedUtcClock());
        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(MigrationIdsThrough006, await ReadMigrationIdsAsync(connection));
        Assert.Equal(before, await ReadPublicationSnapshotAsync(connection));
        await AssertPost006ForeignKeyStateAsync(connection);
        await AssertCurrentStateAsync(connection, catalog);
    }

    [Fact]
    public async Task FailureAfterOldFkDropRollsBackExactlyAndRetryRecords006Once()
    {
        await using var database = await IsolatedMigrationDatabase.CreateAsync();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        var catalog = SqlMigrationCatalog.Load();
        await CreateExactPrefixAsync(connection, catalog, prefixLength: 5);
        await SeedPublicationDataAsync(connection);
        var before = await ReadPublicationSnapshotAsync(connection);

        await ExecuteAsync(
            connection,
            $"""
            CREATE TABLE dbo.C006ConstraintConflict
            (
                Id int NOT NULL,
                CONSTRAINT {NewForeignKeyName} UNIQUE (Id)
            );
            """);

        var engine = new SqlServerMigrationEngine(catalog, new FixedUtcClock());
        var exception = await Assert.ThrowsAsync<MigrationExecutionException>(() =>
            engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Equal(6, exception.MigrationId);
        Assert.Equal("CorrectOperationalMetricProjectionManifestParent", exception.MigrationName);
        Assert.IsType<SqlException>(exception.InnerException);
        Assert.Equal(MigrationIdsThrough005, await ReadMigrationIdsAsync(connection));
        Assert.Equal(before, await ReadPublicationSnapshotAsync(connection));
        await AssertPost005ForeignKeyStateAsync(connection);

        await ExecuteAsync(connection, "DROP TABLE dbo.C006ConstraintConflict;");
        await engine.ApplyAsync(connection, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(MigrationIdsThrough006, await ReadMigrationIdsAsync(connection));
        Assert.Equal(1, await CountMigration006RowsAsync(connection));
        Assert.Equal(before, await ReadPublicationSnapshotAsync(connection));
        await AssertPost006ForeignKeyStateAsync(connection);
        await AssertCurrentStateAsync(connection, catalog);
    }

    private static async Task CreateExactPrefixAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog,
        int prefixLength)
    {
        Assert.InRange(prefixLength, 0, catalog.Migrations.Length);
        await using var transaction = connection.BeginTransaction();
        await SqlServerMigrationLedgerCreator.CreateAsync(connection, transaction, CancellationToken.None);
        var historyStore = new SqlServerMigrationHistoryStore(new FixedUtcClock());

        for (var index = 0; index < prefixLength; index++)
        {
            var migration = catalog.Migrations[index];
            await SqlServerMigrationExecutor.ExecuteAsync(connection, transaction, migration, CancellationToken.None);
            await historyStore.InsertAsync(connection, transaction, migration, CancellationToken.None);
        }

        await transaction.CommitAsync();
    }

    private static async Task SeedPublicationDataAsync(SqlConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO dbo.MetricInputStream
                (MachineId, StreamKeyBinary, StreamKey)
            VALUES
                ('00112233-4455-6677-8899-AABBCCDDEEFF', 0x01, N'stream');

            DECLARE @MetricInputStreamRowId bigint = SCOPE_IDENTITY();

            INSERT INTO dbo.MetricAggregationProcessor
                (ProcessorKeyBinary, ProcessorKey, MetricInputStreamRowId)
            VALUES
                (0x01, N'aggregate', @MetricInputStreamRowId);

            DECLARE @MetricAggregationProcessorRowId bigint = SCOPE_IDENTITY();

            INSERT INTO dbo.OperationalMetricProjectionProcessor
                (ProcessorKeyBinary, ProcessorKey, MetricAggregationProcessorRowId, MetricInputStreamRowId)
            VALUES
                (0x01004100, N'A', @MetricAggregationProcessorRowId, @MetricInputStreamRowId);

            DECLARE @ProjectionProcessorRowId bigint = SCOPE_IDENTITY();

            INSERT INTO dbo.OperationalMetricProjectionCheckpoint
                (OperationalMetricProjectionProcessorRowId, Position)
            VALUES
                (@ProjectionProcessorRowId, 5);

            INSERT INTO dbo.OperationalMetricProjection
            (
                OperationalMetricProjectionProcessorRowId,
                EvaluationKeyCodecVersion,
                EvaluationKeyHash,
                EvaluationKeyBinary,
                MachineId,
                PeriodKind,
                PeriodSiteId,
                PeriodSiteOrderKey,
                ShiftScheduleAssignmentId,
                ShiftScheduleAssignmentOrderKey,
                ShiftId,
                ShiftOrderKey,
                ShiftStartsAtUtc,
                ShiftEndsAtUtc,
                ProductionBusinessDate,
                ProductionOrderPresent,
                ProductionOrderId,
                ProductionOrderOrderKey,
                OperationPresent,
                OperationId,
                OperationOrderKey,
                PartPresent,
                PartId,
                PartOrderKey,
                OperatorPresent,
                OperatorId,
                OperatorOrderKey,
                MetricKey,
                MetricKeyOrderKey,
                DefinitionVersion,
                DefinitionVersionOrderKey,
                Status,
                MetricValue,
                Unit,
                ReasonCode,
                ReasonOperandName,
                SourceRevisionPosition
            )
            VALUES
            (
                @ProjectionProcessorRowId,
                1,
                0x000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F,
                0x01,
                '00112233-4455-6677-8899-AABBCCDDEEFF',
                2,
                N'SITE-1',
                0x01,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                '2026-09-08',
                0,
                NULL,
                NULL,
                0,
                NULL,
                NULL,
                0,
                NULL,
                NULL,
                0,
                NULL,
                NULL,
                N'Availability',
                0x01,
                N'1.0',
                0x01,
                0,
                N'0.95',
                N'ratio',
                NULL,
                NULL,
                5
            );

            DECLARE @ProjectionRowId bigint = SCOPE_IDENTITY();

            INSERT INTO dbo.OperationalMetricProjectionManifest
                (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId)
            VALUES
                (@ProjectionProcessorRowId, @ProjectionRowId);

            INSERT INTO dbo.OperationalMetricProjectionEvidence
            (
                OperationalMetricProjectionRowId,
                EvidenceKind,
                EvidenceOrdinal,
                OperandName,
                OperandNameOrderKey,
                ComponentKey,
                MetricDimension,
                ComponentValue,
                ComponentUnit,
                InputCount,
                FirstInputTimestamp,
                LastInputTimestamp,
                DependencyMetricKey,
                DependencyDefinitionVersion,
                DependencySnapshotCodecVersion,
                DependencySnapshotHash,
                DependencySnapshotBinary
            )
            VALUES
            (
                @ProjectionRowId,
                1,
                0,
                N'run-time',
                0x01,
                N'RunTime',
                1,
                N'95',
                N'minutes',
                1,
                '2026-09-08T00:00:00+00:00',
                '2026-09-08T01:00:00+00:00',
                NULL,
                NULL,
                NULL,
                NULL,
                NULL
            );
            """);
    }

    private static async Task<PublicationSnapshot> ReadPublicationSnapshotAsync(SqlConnection connection)
    {
        return new PublicationSnapshot(
            await ReadJsonAsync(
                connection,
                "SELECT * FROM dbo.OperationalMetricProjectionProcessor ORDER BY OperationalMetricProjectionProcessorRowId FOR JSON PATH, INCLUDE_NULL_VALUES;"),
            await ReadJsonAsync(
                connection,
                "SELECT * FROM dbo.OperationalMetricProjectionCheckpoint ORDER BY OperationalMetricProjectionProcessorRowId FOR JSON PATH, INCLUDE_NULL_VALUES;"),
            await ReadJsonAsync(
                connection,
                "SELECT * FROM dbo.OperationalMetricProjection ORDER BY OperationalMetricProjectionRowId FOR JSON PATH, INCLUDE_NULL_VALUES;"),
            await ReadJsonAsync(
                connection,
                "SELECT * FROM dbo.OperationalMetricProjectionManifest ORDER BY OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId FOR JSON PATH, INCLUDE_NULL_VALUES;"),
            await ReadJsonAsync(
                connection,
                "SELECT * FROM dbo.OperationalMetricProjectionEvidence ORDER BY OperationalMetricProjectionEvidenceRowId FOR JSON PATH, INCLUDE_NULL_VALUES;"));
    }

    private static async Task<string> ReadJsonAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task AssertPost005ForeignKeyStateAsync(SqlConnection connection)
    {
        var oldForeignKey = await ReadForeignKeyAsync(connection, OldForeignKeyName);
        Assert.NotNull(oldForeignKey);
        Assert.Equal("OperationalMetricProjectionManifest", oldForeignKey.ParentTable);
        Assert.Equal("OperationalMetricProjectionCheckpoint", oldForeignKey.ReferencedTable);
        Assert.Equal("OperationalMetricProjectionProcessorRowId", oldForeignKey.ParentColumn);
        Assert.Equal("OperationalMetricProjectionProcessorRowId", oldForeignKey.ReferencedColumn);
        Assert.False(oldForeignKey.IsDisabled);
        Assert.False(oldForeignKey.IsNotTrusted);
        Assert.Null(await ReadForeignKeyAsync(connection, NewForeignKeyName));
    }

    private static async Task AssertPost006ForeignKeyStateAsync(SqlConnection connection)
    {
        Assert.Null(await ReadForeignKeyAsync(connection, OldForeignKeyName));
        var newForeignKey = await ReadForeignKeyAsync(connection, NewForeignKeyName);
        Assert.NotNull(newForeignKey);
        Assert.Equal("OperationalMetricProjectionManifest", newForeignKey.ParentTable);
        Assert.Equal("OperationalMetricProjectionProcessor", newForeignKey.ReferencedTable);
        Assert.Equal("OperationalMetricProjectionProcessorRowId", newForeignKey.ParentColumn);
        Assert.Equal("OperationalMetricProjectionProcessorRowId", newForeignKey.ReferencedColumn);
        Assert.False(newForeignKey.IsDisabled);
        Assert.False(newForeignKey.IsNotTrusted);
    }

    private static async Task<ForeignKeyState?> ReadForeignKeyAsync(
        SqlConnection connection,
        string foreignKeyName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                parent_table.name,
                referenced_table.name,
                parent_column.name,
                referenced_column.name,
                fk.is_disabled,
                fk.is_not_trusted
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.tables AS parent_table
                ON parent_table.object_id = fk.parent_object_id
            INNER JOIN sys.tables AS referenced_table
                ON referenced_table.object_id = fk.referenced_object_id
            INNER JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS parent_column
                ON parent_column.object_id = fkc.parent_object_id
                AND parent_column.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS referenced_column
                ON referenced_column.object_id = fkc.referenced_object_id
                AND referenced_column.column_id = fkc.referenced_column_id
            WHERE fk.name = @ForeignKeyName;
            """;
        command.Parameters.AddWithValue("@ForeignKeyName", foreignKeyName);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var state = new ForeignKeyState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5));
        Assert.False(await reader.ReadAsync());
        return state;
    }

    private static async Task AssertCurrentStateAsync(
        SqlConnection connection,
        SqlMigrationCatalog catalog)
    {
        await using var transaction = connection.BeginTransaction();
        var historyStore = new SqlServerMigrationHistoryStore(new FixedUtcClock());
        var history = await historyStore.ReadAsync(connection, transaction, CancellationToken.None);
        Assert.Equal(
            SqlRuntimeMigrationHistoryClassification.ExactCurrent,
            SqlRuntimeMigrationHistoryClassifier.Classify(history, catalog));

        var schema = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaInTransactionAsync(connection, transaction, CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, schema);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    private static async Task<int[]> ReadMigrationIdsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT MigrationId FROM dbo.FactoryConnectMigrationHistory ORDER BY MigrationId;";
        var values = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        return values.ToArray();
    }

    private static async Task<int> CountMigration006RowsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.FactoryConnectMigrationHistory
            WHERE MigrationId = 6
              AND MigrationName = N'CorrectOperationalMetricProjectionManifestParent'
              AND CanonicalChecksum = N'DDFD8C6CC1FADE9D7486EB5D3D915881A2538E2724A6B57EDA7E6D7C16EB6EEE';
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record PublicationSnapshot(
        string Processor,
        string Checkpoint,
        string Projection,
        string Manifest,
        string Evidence);

    private sealed record ForeignKeyState(
        string ParentTable,
        string ReferencedTable,
        string ParentColumn,
        string ReferencedColumn,
        bool IsDisabled,
        bool IsNotTrusted);

    private sealed class FixedUtcClock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class IsolatedMigrationDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private bool _exists;

        private IsolatedMigrationDatabase(
            string adminConnectionString,
            string databaseName,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
            _exists = true;
        }

        public string ConnectionString { get; }

        public static async Task<IsolatedMigrationDatabase> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_M006_{Guid.NewGuid():N}";
            var adminBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
            {
                InitialCatalog = "master"
            };

            await using (var adminConnection = new SqlConnection(adminBuilder.ConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{EscapeIdentifier(databaseName)}];";
                await command.ExecuteNonQueryAsync();
            }

            var databaseBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
            {
                InitialCatalog = databaseName
            };
            return new IsolatedMigrationDatabase(
                adminBuilder.ConnectionString,
                databaseName,
                databaseBuilder.ConnectionString);
        }

        public SqlConnection CreateConnection() => new(ConnectionString);

        public async ValueTask DisposeAsync()
        {
            if (!_exists)
            {
                return;
            }

            await using var connection = new SqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var escapedIdentifier = EscapeIdentifier(_databaseName);
            var escapedLiteral = EscapeLiteral(_databaseName);
            command.CommandText =
                $"IF DB_ID(N'{escapedLiteral}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{escapedIdentifier}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{escapedIdentifier}]; END;";
            await command.ExecuteNonQueryAsync();
            _exists = false;
        }

        private static string EscapeIdentifier(string value) =>
            value.Replace("]", "]]", StringComparison.Ordinal);

        private static string EscapeLiteral(string value) =>
            value.Replace("'", "''", StringComparison.Ordinal);
    }
}
