using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

internal sealed class SqlServerHistoricalDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private bool _exists;

    private SqlServerHistoricalDatabase(
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

    public static async Task<SqlServerHistoricalDatabase> CreateLegacyPost004Async()
    {
        var database = await CreateEmptyAsync();
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync();
            var catalog = SqlMigrationCatalog.Load();
            await using var transaction = connection.BeginTransaction();
            foreach (var migration in catalog.Migrations.Take(LegacyPost004MigrationHistory.Entries.Length))
            {
                await SqlServerMigrationExecutor.ExecuteAsync(
                    connection,
                    transaction,
                    migration,
                    CancellationToken.None);
            }

            await transaction.CommitAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
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
            $"IF DB_ID(N'{escapedLiteral}') IS NOT NULL " +
            "BEGIN " +
            $"ALTER DATABASE [{escapedIdentifier}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{escapedIdentifier}]; " +
            "END;";
        await command.ExecuteNonQueryAsync();
        _exists = false;
    }

    private static async Task<SqlServerHistoricalDatabase> CreateEmptyAsync()
    {
        var sourceConnectionString = Environment.GetEnvironmentVariable(
            SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(sourceConnectionString))
        {
            throw new InvalidOperationException(
                $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
        }

        var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
        var databaseName = $"FactoryConnect_FC030_Historical_{Guid.NewGuid():N}";
        var adminBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            InitialCatalog = "master",
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
            InitialCatalog = databaseName,
        };

        return new SqlServerHistoricalDatabase(
            adminBuilder.ConnectionString,
            databaseName,
            databaseBuilder.ConnectionString);
    }

    private static string EscapeIdentifier(string value) =>
        value.Replace("]", "]]", StringComparison.Ordinal);

    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
