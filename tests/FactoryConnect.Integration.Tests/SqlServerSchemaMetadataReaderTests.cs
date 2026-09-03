using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerSchemaMetadataReaderContractTests
{
    [Fact]
    public void NormalizeLengthConvertsUnicodeBytesToCharacters()
    {
        var length = SqlServerSchemaMetadataReader.NormalizeLength("nvarchar", 512);

        var bounded = Assert.IsType<SqlLengthDescriptor>(length);
        Assert.False(bounded.IsMax);
        Assert.Equal(256, bounded.Value);
    }

    [Fact]
    public void NormalizeLengthConvertsCatalogMinusOneToMax()
    {
        var length = SqlServerSchemaMetadataReader.NormalizeLength("varbinary", -1);

        var max = Assert.IsType<SqlLengthDescriptor>(length);
        Assert.True(max.IsMax);
        Assert.Null(max.Value);
    }

    [Theory]
    [InlineData("bigint")]
    [InlineData("bit")]
    [InlineData("date")]
    [InlineData("datetimeoffset")]
    [InlineData("uniqueidentifier")]
    public void NormalizeLengthIgnoresTypesWithoutLengthSemantics(string sqlType)
    {
        Assert.Null(SqlServerSchemaMetadataReader.NormalizeLength(sqlType, 8));
    }

    [Theory]
    [InlineData("decimal", (byte)20, (byte)20)]
    [InlineData("numeric", (byte)18, (byte)18)]
    [InlineData("bigint", (byte)19, null)]
    public void NormalizePrecisionRetainsOnlySemanticPrecision(
        string sqlType,
        byte catalogPrecision,
        byte? expected)
    {
        Assert.Equal(expected, SqlServerSchemaMetadataReader.NormalizePrecision(sqlType, catalogPrecision));
    }

    [Theory]
    [InlineData("decimal", (byte)4, (byte)4)]
    [InlineData("datetimeoffset", (byte)7, (byte)7)]
    [InlineData("datetime2", (byte)3, (byte)3)]
    [InlineData("time", (byte)5, (byte)5)]
    [InlineData("bigint", (byte)0, null)]
    public void NormalizeScaleRetainsOnlySemanticScale(
        string sqlType,
        byte catalogScale,
        byte? expected)
    {
        Assert.Equal(expected, SqlServerSchemaMetadataReader.NormalizeScale(sqlType, catalogScale));
    }
}

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSchemaMetadataReaderIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerSchemaMetadataReaderIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReaderProjectsAllPost004OwnedTablesInDeterministicOrder()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var reader = new SqlServerSchemaMetadataReader();

        var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
            connection,
            CancellationToken.None);

        Assert.Equal(13, snapshot.Tables.Length);
        var names = snapshot.Tables.Select(static table => table.Name).ToArray();
        Assert.Equal(
            names.OrderBy(static name => name.SchemaName, StringComparer.Ordinal)
                .ThenBy(static name => name.ObjectName, StringComparer.Ordinal),
            names);
        Assert.Equal(
            SqlRepositorySchemaAuthority.OwnedObjects.OwnedTables,
            names);
    }

    [Fact]
    public async Task MigratedPost004DatabaseExactlyMatchesRepositoryDescriptors()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var actual = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);

        AssertExactMatch(SqlRepositorySchemaDescriptors.LegacyPost004, actual, "LegacyPost004");
        AssertExactMatch(SqlRepositorySchemaDescriptors.Current, actual, "Current");
    }

    [Fact]
    public async Task ReaderNormalizesRepresentativeColumnAndIdentityMetadata()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var reader = new SqlServerSchemaMetadataReader();

        var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
            connection,
            CancellationToken.None);

        var metricInputStream = snapshot.Tables.Single(
            static table => table.Name.ObjectName == "MetricInputStream");
        var rowId = metricInputStream.Columns.Single(
            static column => column.Name == "MetricInputStreamRowId");
        var streamKey = metricInputStream.Columns.Single(
            static column => column.Name == "StreamKey");

        var identity = Assert.IsType<SqlIdentityDescriptor>(rowId.Identity);
        Assert.Equal(1m, identity.SeedValue);
        Assert.Equal(1m, identity.IncrementValue);
        Assert.False(identity.IsNotForReplication);
        var length = Assert.IsType<SqlLengthDescriptor>(streamKey.MaxLength);
        Assert.Equal(256, length.Value);
        Assert.False(length.IsMax);
        Assert.Equal("Latin1_General_100_BIN2", streamKey.Collation);
    }

    [Fact]
    public async Task ReaderProjectsMigration003FinalConstraintState()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var reader = new SqlServerSchemaMetadataReader();

        var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
            connection,
            CancellationToken.None);

        var fact = snapshot.Tables.Single(static table => table.Name.ObjectName == "MetricInputFact");
        var foreignKey = Assert.Single(fact.ForeignKeys);
        Assert.Equal("FK_MetricInputFact_StreamMachine", foreignKey.Name);
        Assert.Collection(
            foreignKey.Columns,
            static column => Assert.Equal("MetricInputStreamRowId", column),
            static column => Assert.Equal("MachineId", column));
        Assert.Collection(
            foreignKey.ReferencedColumns,
            static column => Assert.Equal("MetricInputStreamRowId", column),
            static column => Assert.Equal("MachineId", column));
        Assert.True(foreignKey.IsEnabled);
        Assert.True(foreignKey.IsTrusted);
        Assert.False(foreignKey.IsNotForReplication);
        Assert.Equal(SqlReferentialAction.NoAction, foreignKey.DeleteAction);
        Assert.Equal(SqlReferentialAction.NoAction, foreignKey.UpdateAction);
    }

    [Fact]
    public async Task ReaderProjectsOrdinaryCoveringIndexStructure()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var reader = new SqlServerSchemaMetadataReader();

        var snapshot = await reader.ReadFactoryConnectOwnedSchemaAsync(
            connection,
            CancellationToken.None);

        var fact = snapshot.Tables.Single(static table => table.Name.ObjectName == "MetricInputFact");
        var index = Assert.Single(fact.Indexes);
        Assert.Equal("IX_MetricInputFact_OrderedRead", index.Name);
        Assert.False(index.IsUnique);
        Assert.True(index.IsEnabled);
        Assert.False(index.IndexStructure.IsClustered);
        Assert.Equal(
            ["MetricInputStreamRowId", "Position"],
            index.IndexStructure.KeyColumns.Select(static column => column.Name));
        Assert.All(
            index.IndexStructure.KeyColumns,
            static column => Assert.Equal(SqlIndexColumnDirection.Ascending, column.Direction));
        Assert.Collection(
            index.IndexStructure.IncludedColumns,
            static column => Assert.Equal("MetricInputFactRowId", column),
            static column => Assert.Equal("FactId", column),
            static column => Assert.Equal("MetricInputKey", column),
            static column => Assert.Equal("MetricValue", column),
            static column => Assert.Equal("Unit", column));
    }

    [Fact]
    public async Task ReaderObservesPreCancelledOperation()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        var reader = new SqlServerSchemaMetadataReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadFactoryConnectOwnedSchemaAsync(connection, cancellation.Token));
    }

    private static void AssertExactMatch(
        SqlSchemaDescriptor expected,
        SqlSchemaDescriptor actual,
        string descriptorName)
    {
        var result = SqlSchemaComparator.Compare(expected, actual);
        var diagnostics = string.Join(
            Environment.NewLine,
            result.Differences.Select(static difference =>
                $"{difference.Kind}: {difference.Table.SchemaName}.{difference.Table.ObjectName}.{difference.ArtifactName} — {difference.Detail}"));

        Assert.True(result.IsExactMatch, $"{descriptorName} did not exactly match the migrated post-004 database:{Environment.NewLine}{diagnostics}");
    }
}
