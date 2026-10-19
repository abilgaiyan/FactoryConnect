using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class QualityMigrationAuthorityTests
{
    private static readonly string[] ExpectedTables = ["QualityPolicy", "QualityAssessment"];
    [Fact]
    public void Migration016OnlyAddsSeparateQualityTables()
    {
        var previous = SqlRepositorySchemaDescriptors.Post015;
        var current = SqlRepositorySchemaDescriptors.Post016;
        Assert.Equal(40, previous.Tables.Length); Assert.Equal(42, current.Tables.Length);
        Assert.Equal(previous.Tables, current.Tables.Take(40));
        Assert.Equal(ExpectedTables, current.Tables.Skip(40).Select(static table => table.Name.ObjectName));
        Assert.Same(SqlRepositorySchemaDescriptors.Post021, SqlRepositorySchemaDescriptors.Current);
        Assert.Equal("ImmutableQualityAssessment", SqlMigrationCatalog.Load().Migrations[15].Name);
    }
}
