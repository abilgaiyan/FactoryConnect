using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Collection(SqlStartupConcurrencyDefinition.CollectionName)]
[Trait("Category", "SqlServerIntegration")]
public sealed class SqlPersistenceStartupConcurrentEmptyDatabaseIntegrationTests
{
    private static readonly TimeSpan StartupLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task TwoExplicitMigrationOperationsFromEmptyDatabaseConvergeThenRuntimeReadinessSucceeds()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();

        var firstMigration = SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            StartupLockTimeout,
            CancellationToken.None);
        var secondMigration = SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            StartupLockTimeout,
            CancellationToken.None);

        await Task.WhenAll(
            firstMigration.WaitAsync(CompletionTimeout),
            secondMigration.WaitAsync(CompletionTimeout));

        var apiGate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(StartupLockTimeout));
        var edgeGate = new SqlServerPersistenceStartupGate(
            database.ConnectionString,
            new SqlPersistenceStartupOptions(StartupLockTimeout));

        await Task.WhenAll(
            apiGate.EnsureReadyAsync(CancellationToken.None).AsTask(),
            edgeGate.EnsureReadyAsync(CancellationToken.None).AsTask());

        await AssertCompatibleAsync(database.ConnectionString);
    }

    private static async Task AssertCompatibleAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        var result = await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault().VerifyAsync(
            connection,
            StartupLockTimeout,
            CancellationToken.None);

        Assert.True(result.IsCompatible);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.Empty(result.Diagnostics);
    }
}
