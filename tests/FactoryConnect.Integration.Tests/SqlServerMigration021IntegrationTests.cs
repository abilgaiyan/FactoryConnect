using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration021IntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAndPost020UpgradeMatchAuthorityAndRerunWithoutChangingHistory(bool upgrade)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        string? previous = null;
        if (upgrade) { await SqlMigration020TestBaseline.ApplyAsync(connection); previous = await Prefix(connection); }
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        await AssertCurrent(connection);
        if (upgrade) Assert.Equal(previous, await Prefix(connection));
        var history = await Scalar(connection, "SELECT STRING_AGG(CONCAT(MigrationId,':',CanonicalChecksum,':',CONVERT(nvarchar(48),AppliedAtUtc,127)),',') WITHIN GROUP (ORDER BY MigrationId) FROM dbo.FactoryConnectMigrationHistory;");
        Assert.Equal(21, (int)(await Scalar(connection, "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory;"))!);
        Assert.Equal(0, (int)(await Scalar(connection, "SELECT (SELECT COUNT(*) FROM dbo.OperationalMetricConflictWitnessArchive)+(SELECT COUNT(*) FROM dbo.OperationalMetricPublicationResultArchive)+(SELECT COUNT(*) FROM dbo.OperationalMetricResultWitnessReference);"))!);
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(history, await Scalar(connection, "SELECT STRING_AGG(CONCAT(MigrationId,':',CanonicalChecksum,':',CONVERT(nvarchar(48),AppliedAtUtc,127)),',') WITHIN GROUP (ORDER BY MigrationId) FROM dbo.FactoryConnectMigrationHistory;"));
        var compatibility = await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, compatibility.Classification);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public async Task FailureAtEachCompilationBoundaryRollsBackTheWholeMigration(int stage)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await SqlMigration020TestBaseline.ApplyAsync(connection); var previous = await Prefix(connection);
        var migration = SqlMigrationCatalog.Load().Migrations[20]; var batches = SqlMigrationExecutionBatches.For(migration);
        if (stage < 9) batches[stage] += "\nTHROW 51022, 'P2 injected failure', 1;";
        else batches[stage] = batches[stage].Replace("ON dbo.OperationalMetric", "ON dbo.P2Missing_OperationalMetric", StringComparison.Ordinal);
        var faulty = migration with { CanonicalSql = string.Join("\n" + SqlMigrationExecutionBatches.Marker + "\n", batches) };
        await using (var transaction = connection.BeginTransaction())
        {
            var error = await Assert.ThrowsAsync<MigrationExecutionException>(() => SqlServerMigrationExecutor.ExecuteAsync(connection, transaction, faulty, CancellationToken.None));
            Assert.Equal(21, error.MigrationId);
            await transaction.RollbackAsync();
        }
        Assert.Equal(previous, await Prefix(connection));
        await AssertSchema(connection, SqlRepositorySchemaDescriptors.Post020);
        Assert.Equal(0, (int)(await Scalar(connection, "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId=21;"))!);
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        await AssertCurrent(connection);
    }

    [Fact]
    public async Task CallerCanRollBackAllTwelveSuccessfulCommandsAndCancellationDoesNotCommit()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await SqlMigration020TestBaseline.ApplyAsync(connection);
        var migration = SqlMigrationCatalog.Load().Migrations[20];
        await using (var transaction = connection.BeginTransaction())
        {
            await SqlServerMigrationExecutor.ExecuteAsync(connection, transaction, migration, CancellationToken.None);
            await transaction.RollbackAsync();
        }
        await AssertSchema(connection, SqlRepositorySchemaDescriptors.Post020);
        await using (var transaction = connection.BeginTransaction())
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlServerMigrationExecutor.ExecuteAsync(connection, transaction, migration, cancelled.Token));
            await transaction.RollbackAsync();
        }
        await AssertSchema(connection, SqlRepositorySchemaDescriptors.Post020);
    }

    [Theory]
    [InlineData("DISABLE TRIGGER dbo.TR_OMCWA_Immutable ON dbo.OperationalMetricConflictWitnessArchive;")]
    [InlineData("DROP TRIGGER dbo.TR_OMPRA_Immutable;")]
    [InlineData("ALTER TRIGGER dbo.TR_OMRWR_Immutable ON dbo.OperationalMetricResultWitnessReference AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; END;")]
    [InlineData("ALTER TABLE dbo.OperationalMetricProjection DROP CONSTRAINT DF_OMP_PublicationFormat;")]
    [InlineData("ALTER TABLE dbo.OperationalMetricProjectionProcessor DROP CONSTRAINT DF_OMPP_PublicationFormat; ALTER TABLE dbo.OperationalMetricProjectionProcessor ADD CONSTRAINT DF_OMPP_PublicationFormat DEFAULT (2) FOR PublicationFormatVersion;")]
    public async Task RuntimeAndMigrationEngineRejectImmutabilityOrLegacyDefaultDrift(string mutation)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        await using (var command = connection.CreateCommand()) { command.CommandText = mutation; await command.ExecuteNonQueryAsync(); }
        var result = await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationSchemaDrift, result.Classification);
        await Assert.ThrowsAsync<FinalSchemaValidationException>(() => SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None));
    }

    private static Task AssertCurrent(SqlConnection connection) => AssertSchema(connection, SqlRepositorySchemaDescriptors.Current);
    private static async Task AssertSchema(SqlConnection connection, SqlSchemaDescriptor expected)
    {
        var actual = await new SqlServerSchemaMetadataReader().ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(expected, actual);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
    }
    private static async Task<string> Prefix(SqlConnection c) => (string)(await Scalar(c, "SELECT STRING_AGG(CONCAT(MigrationId,':',CanonicalChecksum,':',CONVERT(nvarchar(48),AppliedAtUtc,127)),',') WITHIN GROUP (ORDER BY MigrationId) FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId<=20;"))!;
    private static async Task<object?> Scalar(SqlConnection c, string sql) { await using var command = c.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
}
