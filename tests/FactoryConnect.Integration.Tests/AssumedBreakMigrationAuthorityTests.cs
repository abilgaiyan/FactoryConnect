using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class AssumedBreakMigrationAuthorityTests
{
    [Fact]
    public void Migration018OnlyAddsAssumedBreakStorage()
    {
        var previous = SqlRepositorySchemaDescriptors.Post017;
        var current = SqlRepositorySchemaDescriptors.Post018;
        Assert.Equal(44, current.Tables.Length);
        Assert.Equal(previous.Tables, current.Tables.Take(previous.Tables.Length));
        Assert.Equal("AssumedBreakAssessment", current.Tables[^1].Name.ObjectName);
        Assert.Same(SqlRepositorySchemaDescriptors.Post022, SqlRepositorySchemaDescriptors.Current);
        Assert.Equal("ImmutableAssumedBreakAssessment", SqlMigrationCatalog.Load().Migrations.Single(x => x.MigrationId == 18).Name);
    }
}
