using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerTestDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable =
        "FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING";

    private string? _adminConnectionString;
    private string? _databaseName;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var sourceConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(sourceConnectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringEnvironmentVariable} is required for " +
                "SQL Server integration tests.");
        }

        var sourceBuilder = new SqlConnectionStringBuilder(
            sourceConnectionString);

        if (string.IsNullOrWhiteSpace(sourceBuilder.DataSource))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringEnvironmentVariable} must specify a SQL Server data source.");
        }

        _databaseName = $"FactoryConnect_FC023_{Guid.NewGuid():N}";

        var adminBuilder = new SqlConnectionStringBuilder(
            sourceBuilder.ConnectionString)
        {
            InitialCatalog = "master",
        };

        _adminConnectionString = adminBuilder.ConnectionString;

        await using (var adminConnection = new SqlConnection(
            _adminConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var createCommand = adminConnection.CreateCommand();
            createCommand.CommandText =
                $"CREATE DATABASE [{EscapeIdentifier(_databaseName)}]";
            await createCommand.ExecuteNonQueryAsync();
        }

        var databaseBuilder = new SqlConnectionStringBuilder(
            sourceBuilder.ConnectionString)
        {
            InitialCatalog = _databaseName,
        };

        ConnectionString = databaseBuilder.ConnectionString;

        await using var databaseConnection = new SqlConnection(
            ConnectionString);
        await databaseConnection.OpenAsync();
        await using var schemaCommand = databaseConnection.CreateCommand();
        schemaCommand.CommandText = SqlServerSchema.ReadInitialSchema();
        await schemaCommand.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_databaseName is null || _adminConnectionString is null)
        {
            return;
        }

        await using var connection = new SqlConnection(
            _adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var escapedName = EscapeIdentifier(_databaseName);
        command.CommandText =
            $"ALTER DATABASE [{escapedName}] SET SINGLE_USER " +
            "WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{escapedName}];";
        await command.ExecuteNonQueryAsync();
    }

    public SqlConnection CreateConnection() => new(ConnectionString);

    private static string EscapeIdentifier(string value) =>
        value.Replace("]", "]]", StringComparison.Ordinal);
}
