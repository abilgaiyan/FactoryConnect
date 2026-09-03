using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSchemaCheckDefinitionAuthorityTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerSchemaCheckDefinitionAuthorityTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MigratedPost004CheckDefinitionsMatchRepositoryAuthorityLexically()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var actual = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);

        var differences = DescribeCheckDefinitionDifferences(
            SqlRepositorySchemaDescriptors.LegacyPost004,
            actual);

        Assert.True(
            differences.Length == 0,
            string.Join(Environment.NewLine, differences));
    }

    private static string[] DescribeCheckDefinitionDifferences(
        SqlSchemaDescriptor expected,
        SqlSchemaDescriptor actual)
    {
        var actualTables = actual.Tables.ToDictionary(static table => table.Name);
        var differences = new List<string>();

        foreach (var expectedTable in expected.Tables
                     .OrderBy(static table => table.Name.SchemaName, StringComparer.Ordinal)
                     .ThenBy(static table => table.Name.ObjectName, StringComparer.Ordinal))
        {
            if (!actualTables.TryGetValue(expectedTable.Name, out var actualTable))
            {
                continue;
            }

            var actualChecks = actualTable.CheckConstraints.ToDictionary(
                static constraint => constraint.Name,
                StringComparer.Ordinal);

            foreach (var expectedCheck in expectedTable.CheckConstraints
                         .OrderBy(static constraint => constraint.Name, StringComparer.Ordinal))
            {
                if (!actualChecks.TryGetValue(expectedCheck.Name, out var actualCheck))
                {
                    continue;
                }

                var expectedCanonical = SqlFragmentCanonicalizer.Canonicalize(
                    expectedCheck.CanonicalDefinition);
                var actualCanonical = SqlFragmentCanonicalizer.Canonicalize(
                    actualCheck.CanonicalDefinition);

                if (!string.Equals(expectedCanonical, actualCanonical, StringComparison.Ordinal))
                {
                    differences.Add(
                        $"{expectedTable.Name.SchemaName}.{expectedTable.Name.ObjectName}.{expectedCheck.Name}:{Environment.NewLine}" +
                        $"  expected: {expectedCanonical}{Environment.NewLine}" +
                        $"  actual:   {actualCanonical}");
                }
            }
        }

        return [.. differences];
    }
}
