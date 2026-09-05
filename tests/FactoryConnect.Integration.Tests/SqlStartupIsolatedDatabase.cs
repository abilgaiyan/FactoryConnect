using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

internal sealed class SqlStartupIsolatedDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private bool _created;

    private SqlStartupIsolatedDatabase(
        string adminConnectionString,
        string databaseName,
        string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<SqlStartupIsolatedDatabase> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        var sourceConnectionString = Environment.GetEnvironmentVariable(
            SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(sourceConnectionString))
        {
            throw new InvalidOperationException(
                $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
        }

        var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
        if (string.IsNullOrWhiteSpace(sourceBuilder.DataSource))
        {
            throw new InvalidOperationException(
                $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} must specify a SQL Server data source.");
        }

        var databaseName = $"FactoryConnect_FC030E5_{Guid.NewGuid():N}";
        var adminBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            InitialCatalog = "master",
        };
        var databaseBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            InitialCatalog = databaseName,
        };

        var database = new SqlStartupIsolatedDatabase(
            adminBuilder.ConnectionString,
            databaseName,
            databaseBuilder.ConnectionString);

        try
        {
            await using var connection = new SqlConnection(database._adminConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{EscapeIdentifier(databaseName)}]";
            await command.ExecuteNonQueryAsync(cancellationToken);
            database._created = true;
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_created)
        {
            return;
        }

        await using var connection = new SqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var escapedName = EscapeIdentifier(_databaseName);
        command.CommandText =
            $"IF DB_ID(N'{EscapeLiteral(_databaseName)}') IS NOT NULL " +
            "BEGIN " +
            $"ALTER DATABASE [{escapedName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{escapedName}]; " +
            "END";
        await command.ExecuteNonQueryAsync();
        _created = false;
    }

    private static string EscapeIdentifier(string value) =>
        value.Replace("]", "]]", StringComparison.Ordinal);

    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
