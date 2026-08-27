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
    private bool _databaseCreated;

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

        _databaseName = $"FactoryConnect_FC026_{Guid.NewGuid():N}";

        var adminBuilder = new SqlConnectionStringBuilder(
            sourceBuilder.ConnectionString)
        {
            InitialCatalog = "master",
        };

        _adminConnectionString = adminBuilder.ConnectionString;

        try
        {
            await CreateDatabaseAsync();

            var databaseBuilder = new SqlConnectionStringBuilder(
                sourceBuilder.ConnectionString)
            {
                InitialCatalog = _databaseName,
            };

            ConnectionString = databaseBuilder.ConnectionString;

            await using var databaseConnection = new SqlConnection(
                ConnectionString);
            await databaseConnection.OpenAsync();
            await ExecuteSchemaAsync(
                databaseConnection,
                SqlServerSchema.ReadInitialSchema());
            await ExecuteSchemaAsync(
                databaseConnection,
                SqlServerSchema.ReadMetricAggregationSchema());
            await ExecuteSchemaAsync(
                databaseConnection,
                SqlServerSchema.ReadMetricInputMachineBindingSchema());
        }
        catch
        {
            await DropDatabaseIfCreatedAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        await DropDatabaseIfCreatedAsync();
    }

    public SqlConnection CreateConnection() => new(ConnectionString);

    private static async Task ExecuteSchemaAsync(
        SqlConnection connection,
        string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateDatabaseAsync()
    {
        await using var adminConnection = new SqlConnection(
            _adminConnectionString);
        await adminConnection.OpenAsync();
        await using var createCommand = adminConnection.CreateCommand();
        createCommand.CommandText =
            $"CREATE DATABASE [{EscapeIdentifier(_databaseName!)}]";
        await createCommand.ExecuteNonQueryAsync();
        _databaseCreated = true;
    }

    private async Task DropDatabaseIfCreatedAsync()
    {
        if (!_databaseCreated ||
            _databaseName is null ||
            _adminConnectionString is null)
        {
            return;
        }

        await using var connection = new SqlConnection(
            _adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var escapedName = EscapeIdentifier(_databaseName);
        command.CommandText =
            $"IF DB_ID(N'{EscapeLiteral(_databaseName)}') IS NOT NULL " +
            "BEGIN " +
            $"ALTER DATABASE [{escapedName}] SET SINGLE_USER " +
            "WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{escapedName}]; " +
            "END";
        await command.ExecuteNonQueryAsync();
        _databaseCreated = false;
    }

    private static string EscapeIdentifier(string value) =>
        value.Replace("]", "]]", StringComparison.Ordinal);

    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
