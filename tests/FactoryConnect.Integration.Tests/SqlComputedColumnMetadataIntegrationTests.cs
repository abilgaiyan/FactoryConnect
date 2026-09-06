using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlComputedColumnMetadataIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string FrozenMachineOrderExpression =
        "CONVERT(binary(16), REPLACE(CONVERT(char(36), MachineId), '-', ''), 2)";

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlComputedColumnMetadataIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReaderProjectsComputedDefinitionAndPersistenceFromRealSql()
    {
        var actual = await ReadTemporaryColumnAsync(
            $"AS ({FrozenMachineOrderExpression}) PERSISTED");
        var computed = Assert.IsType<SqlComputedDescriptor>(actual.Computed);

        Assert.True(computed.IsPersisted);
        Assert.False(string.IsNullOrWhiteSpace(computed.Definition));

        var expected = actual with
        {
            Computed = new SqlComputedDescriptor(FrozenMachineOrderExpression, IsPersisted: true),
        };

        Assert.True(SqlSchemaComparator.Compare(Schema(expected), Schema(actual)).IsExactMatch);
    }

    [Fact]
    public async Task RealSqlRejectsExpectedComputedActualOrdinary()
    {
        var expected = await ReadTemporaryColumnAsync(
            $"AS ({FrozenMachineOrderExpression}) PERSISTED");
        var actual = await ReadTemporaryColumnAsync("binary(16) NULL");

        AssertComputedMismatch(expected, actual);
    }

    [Fact]
    public async Task RealSqlRejectsExpectedOrdinaryActualComputed()
    {
        var expected = await ReadTemporaryColumnAsync("binary(16) NULL");
        var actual = await ReadTemporaryColumnAsync(
            $"AS ({FrozenMachineOrderExpression}) PERSISTED");

        AssertComputedMismatch(expected, actual);
    }

    [Fact]
    public async Task RealSqlRejectsPersistedStateRemoval()
    {
        var expected = await ReadTemporaryColumnAsync(
            $"AS ({FrozenMachineOrderExpression}) PERSISTED");
        var actual = await ReadTemporaryColumnAsync(
            $"AS ({FrozenMachineOrderExpression})");

        AssertComputedMismatch(expected, actual);
    }

    [Fact]
    public async Task RealSqlRejectsComputedExpressionDrift()
    {
        var expected = await ReadTemporaryColumnAsync(
            $"AS ({FrozenMachineOrderExpression}) PERSISTED");
        var actual = await ReadTemporaryColumnAsync(
            "AS (CONVERT(binary(16), MachineId)) PERSISTED");

        AssertComputedMismatch(expected, actual);
    }

    [Fact]
    public async Task RealSqlRejectsMachineOrderKeyOrdinaryBinary16Substitution()
    {
        var expected = await ReadTemporaryColumnAsync(
            $"AS ({FrozenMachineOrderExpression}) PERSISTED");
        var actual = await ReadTemporaryColumnAsync("binary(16) NULL");

        Assert.Equal("MachineOrderKey", actual.Name);
        Assert.Equal("binary", actual.SqlType);
        var length = Assert.IsType<SqlLengthDescriptor>(actual.MaxLength);
        Assert.Equal(16, length.Value);
        Assert.Null(actual.Computed);
        AssertComputedMismatch(expected, actual);
    }

    private async Task<SqlColumnDescriptor> ReadTemporaryColumnAsync(string columnDefinition)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"ALTER TABLE dbo.MetricInputFact ADD MachineOrderKey {columnDefinition};";
            await command.ExecuteNonQueryAsync();

            var snapshot = await new SqlServerSchemaMetadataReader()
                .ReadFactoryConnectOwnedSchemaInTransactionAsync(
                    connection,
                    transaction,
                    CancellationToken.None);

            return snapshot.Tables
                .Single(static table => table.Name.ObjectName == "MetricInputFact")
                .Columns
                .Single(static column => column.Name == "MachineOrderKey");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static void AssertComputedMismatch(
        SqlColumnDescriptor expected,
        SqlColumnDescriptor actual)
    {
        var differences = SqlSchemaComparator.Compare(Schema(expected), Schema(actual)).Differences;
        Assert.Contains(
            differences,
            static difference => difference.Kind == SqlSchemaDifferenceKind.ColumnComputedMismatch);
    }

    private static SqlSchemaDescriptor Schema(SqlColumnDescriptor column) => new(
        [new SqlTableDescriptor(
            new SqlObjectName("dbo", "ComputedAuthority"),
            [column],
            PrimaryKey: null,
            UniqueConstraints: [],
            ForeignKeys: [],
            CheckConstraints: [],
            Indexes: [])]);
}
