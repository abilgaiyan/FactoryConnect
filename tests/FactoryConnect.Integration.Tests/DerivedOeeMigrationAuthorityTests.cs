using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class DerivedOeeMigrationAuthorityTests
{
    [Fact]
    public void Migration017OnlyAddsDerivedResultStorage()
    {
        var previous = SqlRepositorySchemaDescriptors.Post016;
        var current = SqlRepositorySchemaDescriptors.Post017;
        Assert.Equal(42, previous.Tables.Length); Assert.Equal(43, current.Tables.Length);
        Assert.Equal(previous.Tables, current.Tables.Take(42));
        Assert.Equal("DerivedOeeAssessment", current.Tables[^1].Name.ObjectName);
        Assert.Same(SqlRepositorySchemaDescriptors.Post018, SqlRepositorySchemaDescriptors.Current);
        Assert.Equal("ImmutableDerivedOeeAssessment", SqlMigrationCatalog.Load().Migrations.Single(x => x.MigrationId == 17).Name);
    }
}
