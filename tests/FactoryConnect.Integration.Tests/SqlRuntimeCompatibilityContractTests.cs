using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlRuntimeCompatibilityContractTests
{
    private static readonly SqlRuntimeCompatibilityClassification[] ExpectedClassifications =
    [
        SqlRuntimeCompatibilityClassification.Compatible,
        SqlRuntimeCompatibilityClassification.DatabaseUninitialized,
        SqlRuntimeCompatibilityClassification.LegacyAdoptionRequired,
        SqlRuntimeCompatibilityClassification.UnledgeredSchemaIncompatible,
        SqlRuntimeCompatibilityClassification.MigrationPending,
        SqlRuntimeCompatibilityClassification.DatabaseNewerThanSupported,
        SqlRuntimeCompatibilityClassification.MigrationIdentityMismatch,
        SqlRuntimeCompatibilityClassification.MigrationChecksumMismatch,
        SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid,
        SqlRuntimeCompatibilityClassification.MigrationSchemaDrift
    ];

    private static readonly SqlRuntimeCompatibilityDecisionStage[] ExpectedDecisionPrecedence =
    [
        SqlRuntimeCompatibilityDecisionStage.LedgerIdentityAndPhysicalShape,
        SqlRuntimeCompatibilityDecisionStage.LedgerRowSemantics,
        SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
        SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification,
        SqlRuntimeCompatibilityDecisionStage.CurrentSchemaComparison
    ];

    [Fact]
    public void ClassificationVocabularyIsClosedAndOrdered()
    {
        Assert.Equal(ExpectedClassifications, Enum.GetValues<SqlRuntimeCompatibilityClassification>());
    }

    [Fact]
    public void DecisionPrecedenceIsClosedAndDeterministic()
    {
        Assert.Equal(
            ExpectedDecisionPrecedence,
            SqlRuntimeCompatibilityDecisionPrecedence.OrderedStages.ToArray());
    }

    [Theory]
    [InlineData(SqlRuntimeCompatibilityClassification.Compatible, true)]
    [InlineData(SqlRuntimeCompatibilityClassification.DatabaseUninitialized, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.LegacyAdoptionRequired, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.UnledgeredSchemaIncompatible, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.MigrationPending, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.DatabaseNewerThanSupported, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.MigrationIdentityMismatch, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.MigrationChecksumMismatch, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid, false)]
    [InlineData(SqlRuntimeCompatibilityClassification.MigrationSchemaDrift, false)]
    public void ResultCompatibilityDependsOnlyOnCompatibleClassification(
        SqlRuntimeCompatibilityClassification classification,
        bool expectedIsCompatible)
    {
        var result = new SqlRuntimeCompatibilityResult(classification);

        Assert.Equal(classification, result.Classification);
        Assert.Equal(expectedIsCompatible, result.IsCompatible);
    }
}
