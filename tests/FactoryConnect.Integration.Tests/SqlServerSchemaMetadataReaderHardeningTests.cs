using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerSchemaMetadataReaderHardeningTests
{
    [Theory]
    [InlineData((byte)1, true)]
    [InlineData((byte)2, false)]
    public void SupportedRowstoreIndexTypesMapExplicitly(
        byte indexType,
        bool expectedClustered)
    {
        Assert.Equal(
            expectedClustered,
            SqlServerSchemaMetadataReader.MapIndexClusteredness(indexType));
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    [InlineData((byte)7)]
    public void UnsupportedIndexTypesAreRejectedDeterministically(byte indexType)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SqlServerSchemaMetadataReader.MapIndexClusteredness(indexType));

        Assert.Equal(
            $"Unsupported SQL Server index type '{indexType}'.",
            exception.Message);
    }
}

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSchemaMetadataReaderHardeningIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerSchemaMetadataReaderHardeningIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReaderExecutesForeignKeyActionAndCompositeKeyOrdinalPaths()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var reader = new SqlServerSchemaMetadataReader();

        var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
            connection,
            CancellationToken.None);

        var fact = snapshot.Tables.Single(
            static table => table.Name.ObjectName == "MetricInputFact");
        var foreignKey = Assert.Single(fact.ForeignKeys);
        Assert.Equal(SqlReferentialAction.NoAction, foreignKey.DeleteAction);
        Assert.Equal(SqlReferentialAction.NoAction, foreignKey.UpdateAction);

        var index = Assert.Single(fact.Indexes);
        Assert.Equal(2, index.IndexStructure.KeyColumns.Length);
        Assert.Equal(1, index.IndexStructure.KeyColumns[0].Ordinal);
        Assert.Equal(2, index.IndexStructure.KeyColumns[1].Ordinal);
    }
}
