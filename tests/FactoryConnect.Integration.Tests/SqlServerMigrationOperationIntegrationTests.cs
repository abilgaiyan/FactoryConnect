using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerMigrationOperationIntegrationTests
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task EmptyExistingDatabaseMigratesToExactRepositoryCurrent()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();

        await SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            LockTimeout,
            CancellationToken.None);

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        var result = await SqlServerRuntimeSchemaCompatibilityVerifier
            .CreateDefault()
            .VerifyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.True(result.IsCompatible);
        Assert.Equal(SqlRuntimeCompatibilityClassification.Compatible, result.Classification);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task ExactCurrentDatabaseCanBeMigratedAgainIdempotently()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();

        await SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            LockTimeout,
            CancellationToken.None);
        await SqlServerMigrationOperation.ApplyAsync(
            database.ConnectionString,
            LockTimeout,
            CancellationToken.None);

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        var result = await SqlServerRuntimeSchemaCompatibilityVerifier
            .CreateDefault()
            .VerifyAsync(connection, LockTimeout, CancellationToken.None);

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task MissingConnectionStringFailsBeforeOpeningSqlConnection()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            SqlServerMigrationOperation.ApplyAsync(
                " ",
                LockTimeout,
                CancellationToken.None));

        Assert.Equal("connectionString", exception.ParamName);
    }

    [Fact]
    public async Task InvalidLockTimeoutFailsBeforeOpeningSqlConnection()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SqlServerMigrationOperation.ApplyAsync(
                "Server=not-used;",
                TimeSpan.FromTicks(-1),
                CancellationToken.None));
    }
}
