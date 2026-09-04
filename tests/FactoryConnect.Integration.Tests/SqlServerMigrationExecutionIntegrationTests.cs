using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigrationExecutionIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMigrationExecutionIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TransactionAwareSchemaReaderMatchesCurrentDescriptor()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var reader = new SqlServerSchemaMetadataReader();

        var actual = await reader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, actual);

        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task LedgerCreatorProducesExactContractAndRollsBackAtomically()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using (var transaction = connection.BeginTransaction())
        {
            await SqlServerMigrationLedgerCreator.CreateAsync(connection, transaction, CancellationToken.None);
            var reader = new SqlServerMigrationLedgerMetadataReader();
            var state = await reader.ResolveObjectAsync(connection, transaction, CancellationToken.None);

            Assert.Equal(SqlMigrationLedgerObjectKind.UserTable, state.Kind);
            var snapshot = await reader.ReadSchemaAsync(
                connection,
                transaction,
                Assert.IsType<int>(state.ObjectId),
                CancellationToken.None);
            SqlMigrationLedgerSchemaValidator.Validate(snapshot);

            await transaction.RollbackAsync();
        }

        await using var verificationTransaction = connection.BeginTransaction();
        var verificationReader = new SqlServerMigrationLedgerMetadataReader();
        var verificationState = await verificationReader.ResolveObjectAsync(
            connection,
            verificationTransaction,
            CancellationToken.None);
        Assert.Equal(SqlMigrationLedgerObjectKind.Absent, verificationState.Kind);
        await verificationTransaction.RollbackAsync();
    }

    [Fact]
    public async Task CanonicalMigrationSqlExecutesInsideCallerTransaction()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using (var transaction = connection.BeginTransaction())
        {
            var migration = CreateDescriptor(
                900,
                "CanonicalExecutionProbe",
                "CREATE TABLE dbo.C3CanonicalExecutionProbe (Id int NOT NULL);");

            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT OBJECT_ID(N'dbo.C3CanonicalExecutionProbe', N'U');";
            var objectId = await command.ExecuteScalarAsync();
            Assert.NotNull(objectId);
            Assert.NotEqual(DBNull.Value, objectId);

            await transaction.RollbackAsync();
        }

        await using var verificationCommand = connection.CreateCommand();
        verificationCommand.CommandText = "SELECT OBJECT_ID(N'dbo.C3CanonicalExecutionProbe', N'U');";
        var persistedObjectId = await verificationCommand.ExecuteScalarAsync();
        Assert.Equal(DBNull.Value, persistedObjectId);
    }

    [Fact]
    public async Task ProviderFailureIsWrappedWithFrozenMigrationIdentity()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var migration = CreateDescriptor(
            901,
            "ExpectedFailure",
            "SELECT * FROM dbo.C3_Table_That_Does_Not_Exist;");

        var exception = await Assert.ThrowsAsync<MigrationExecutionException>(() =>
            SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None));

        Assert.Equal(901, exception.MigrationId);
        Assert.Equal("ExpectedFailure", exception.MigrationName);
        Assert.IsType<SqlException>(exception.InnerException);
        await transaction.RollbackAsync();
    }

    private static SqlMigrationDescriptor CreateDescriptor(
        int migrationId,
        string name,
        string canonicalSql) =>
        new(
            migrationId,
            name,
            $"FactoryConnect.Persistence.SqlServer.Sql.{migrationId:000}_{name}.sql",
            SqlMigrationTransactionPolicy.EngineOwned,
            canonicalSql,
            ImmutableArray.CreateRange(System.Text.Encoding.UTF8.GetBytes(canonicalSql)),
            new string('A', 64));
}
