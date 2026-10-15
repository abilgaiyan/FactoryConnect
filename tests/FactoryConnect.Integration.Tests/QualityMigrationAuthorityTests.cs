using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class QualityMigrationAuthorityTests
{
    [Fact]
    public void Migration016OnlyAddsSeparateQualityTables()
    {
        var previous = SqlRepositorySchemaDescriptors.Post015;
        var current = SqlRepositorySchemaDescriptors.Post016;
        Assert.Equal(40, previous.Tables.Length); Assert.Equal(42, current.Tables.Length);
        Assert.Equal(previous.Tables, current.Tables.Take(40));
        Assert.Equal(new[] { "QualityPolicy", "QualityAssessment" }, current.Tables.Skip(40).Select(static table => table.Name.ObjectName));
        Assert.Same(current, SqlRepositorySchemaDescriptors.Current);
        Assert.Equal("ImmutableQualityAssessment", SqlMigrationCatalog.Load().Migrations[^1].Name);
    }
}
