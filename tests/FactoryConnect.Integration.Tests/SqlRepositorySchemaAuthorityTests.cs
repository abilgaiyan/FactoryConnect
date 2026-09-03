using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlRepositorySchemaAuthorityTests
{
    private const string CreateTablePrefix = "CREATE TABLE dbo.";

    private static readonly string[] ExpectedOwnedTableNames =
    [
        "ContextualizedActivityOutput",
        "MachineObservation",
        "MetricAggregationCheckpoint",
        "MetricAggregationContribution",
        "MetricAggregationProcessor",
        "MetricInputFact",
        "MetricInputStream",
        "ObservationStreamCheckpoint",
        "ProductionContextCheckpoint",
        "ProductionContextProcessor",
        "ProductionDayMetricAggregate",
        "ProductionTimeEligibilityOutput",
        "ShiftMetricAggregate"
    ];

    [Fact]
    public void OwnedObjectsFreezePost004RepositoryTableRecognitionAuthority()
    {
        var ownedTables = SqlRepositorySchemaAuthority.OwnedObjects.OwnedTables;

        Assert.Equal(13, ownedTables.Length);
        Assert.All(ownedTables, static table => Assert.Equal("dbo", table.SchemaName));
        Assert.Equal(ExpectedOwnedTableNames, ownedTables.Select(static table => table.ObjectName));
    }

    [Fact]
    public void OwnedObjectsMatchTablesCreatedByLegacyRepositoryMigrations()
    {
        var createdTables = SqlMigrationCatalog.Load().Migrations
            .SelectMany(static migration => ExtractCreatedDboTables(migration.CanonicalSql))
            .Distinct()
            .OrderBy(static table => table.SchemaName, StringComparer.Ordinal)
            .ThenBy(static table => table.ObjectName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(SqlRepositorySchemaAuthority.OwnedObjects.OwnedTables, createdTables);
    }

    [Fact]
    public void OwnedObjectsExposeRepositoryMembershipWithoutCatalogCaseResolution()
    {
        var ownedObjects = SqlRepositorySchemaAuthority.OwnedObjects;

        Assert.True(ownedObjects.ContainsRepositoryIdentity(new SqlObjectName("dbo", "MetricInputFact")));
        Assert.False(ownedObjects.ContainsRepositoryIdentity(new SqlObjectName("dbo", "metricinputfact")));
        Assert.False(ownedObjects.ContainsRepositoryIdentity(new SqlObjectName("dbo", "CustomerOrders")));
        Assert.False(ownedObjects.ContainsRepositoryIdentity(new SqlObjectName("audit", "MetricInputFact")));
    }

    private static IEnumerable<SqlObjectName> ExtractCreatedDboTables(string canonicalSql)
    {
        using var reader = new StringReader(canonicalSql);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith(CreateTablePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var tableName = line[CreateTablePrefix.Length..].Trim();
            if (tableName.Length == 0)
            {
                throw new InvalidOperationException("Legacy migration contains an empty CREATE TABLE identity.");
            }

            yield return new SqlObjectName("dbo", tableName);
        }
    }
}
