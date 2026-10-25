using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration022IntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionDefaultIsReadAndMissingOrChangedDefaultIsRejected(bool replaceWithWrongDefault)
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var engine = SqlServerMigrationEngine.CreateDefault();
        await engine.ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);

        var schema = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);
        var table = Assert.Single(schema.Tables, table => table.Name.ObjectName == "ProductionContextProcessor");
        var actual = Assert.Single(table.DefaultConstraints);
        Assert.Equal("DF_ProductionContextProcessor_AdmissionBound", actual.Name);
        Assert.Equal("AdmissionBound", actual.ColumnName);
        Assert.Equal("((0))", actual.Definition);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, schema);
        Assert.True(comparison.IsExactMatch, string.Join(Environment.NewLine, comparison.Differences));

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE dbo.ProductionContextProcessor DROP CONSTRAINT DF_ProductionContextProcessor_AdmissionBound;";
            if (replaceWithWrongDefault)
                command.CommandText += "ALTER TABLE dbo.ProductionContextProcessor ADD CONSTRAINT DF_ProductionContextProcessor_AdmissionBound DEFAULT (1) FOR AdmissionBound;";
            await command.ExecuteNonQueryAsync();
        }
        var compatibility = await SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault()
            .VerifyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(SqlRuntimeCompatibilityClassification.MigrationSchemaDrift, compatibility.Classification);
        await Assert.ThrowsAsync<FinalSchemaValidationException>(() => engine.ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None));
    }
}
