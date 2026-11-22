using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlSupervisorProductionReadinessContractTests
{
    private static string Sql => Assert.Single(SqlMigrationCatalog.Load().Migrations,
        migration => migration.MigrationId == 39).CanonicalSql;

    [Fact]
    public void InspectionReadsExactCurrentRevisionAndItsOwnReview()
    {
        Assert.Contains("ROW_NUMBER() OVER", Sql);
        Assert.Contains("revision.RowNumber = 1", Sql);
        Assert.Contains("revisionReview.RevisionId = revision.RevisionId", Sql);
        Assert.Contains("CurrentRevisionReviewMissing", Sql);
        Assert.Contains("CurrentRevisionRejected", Sql);
        Assert.Contains("CASE WHEN revision.RevisionId IS NULL THEN e.GoodQuantity ELSE revision.GoodQuantity END", Sql);
        Assert.Contains("CASE WHEN revision.RevisionId IS NULL THEN e.RejectedQuantity ELSE revision.RejectedQuantity END", Sql);
    }

    [Fact]
    public void LocalApprovalNeverEstablishesIndependentPublicationAuthority()
    {
        Assert.Contains("IndependentVerificationMissing", Sql);
        Assert.Contains("CoverageAndPopulationReconciliationNotEstablished", Sql);
        Assert.Contains("CAST(0 AS bit) AS PublicationAuthorized", Sql);
        Assert.DoesNotContain("INSERT ", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC dbo.PublishSupervisorAcceptedProduction", Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectionPreservesMissingAndPublishedStates()
    {
        Assert.Contains("EntryNotFound", Sql);
        Assert.Contains("LocalReviewMissing", Sql);
        Assert.Contains("QuantitiesMissing", Sql);
        Assert.Contains("QualificationReceiptMissing", Sql);
        Assert.Contains("QualificationRejected", Sql);
        Assert.Contains("AlreadyPublished", Sql);
    }
}
