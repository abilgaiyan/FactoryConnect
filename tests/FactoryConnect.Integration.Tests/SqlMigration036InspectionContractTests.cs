using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigration036InspectionContractTests
{
    private static readonly string Sql = SqlMigrationCatalog.Load().Migrations
        .Single(migration => migration.MigrationId == 36).CanonicalSql;

    [Fact]
    public void InspectionDoesNotPublishOrQualifyProduction()
    {
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.InspectSupervisorAcceptedProductionReceipt", Sql);
        Assert.Contains("LEFT JOIN dbo.SupervisorProductionQualificationReceipt", Sql);
        Assert.Contains("N'ReceiptNotFound'", Sql);
        Assert.Contains("N'NotQualified'", Sql);
        Assert.Contains("N'AlreadyPublished'", Sql);
        Assert.Contains("N'PopulationAlreadyPublished'", Sql);
        Assert.Contains("N'ShiftAlreadyPublished'", Sql);
        Assert.Contains("N'RequiresCoverageAndAuthorityVerification'", Sql);
        Assert.DoesNotContain("INSERT INTO", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE dbo.", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MERGE dbo.", Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectionDoesNotMistakeQualifiedReceiptForPublicationAuthority()
    {
        Assert.Contains("r.QualificationDecision <> N'Qualified'", Sql);
        Assert.Contains("other.PopulationKey = r.PopulationKey", Sql);
        Assert.Contains("other.MachineId = r.MachineId", Sql);
        Assert.Contains("s.ReceiptId = r.ReceiptId", Sql);
        Assert.Contains("p.ShiftStartsAtUtc = r.ShiftStartsAtUtc", Sql);
        Assert.Contains("p.ShiftEndsAtUtc = r.ShiftEndsAtUtc", Sql);
        Assert.DoesNotContain("N'EligibleToPublish'", Sql);
    }
}
