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
        SqlRuntimeCompatibilityClassification.MigrationHistoryInvalid,
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
    [InlineData("Compatible", true)]
    [InlineData("DatabaseUninitialized", false)]
    [InlineData("LegacyAdoptionRequired", false)]
    [InlineData("UnledgeredSchemaIncompatible", false)]
    [InlineData("MigrationPending", false)]
    [InlineData("DatabaseNewerThanSupported", false)]
    [InlineData("MigrationIdentityMismatch", false)]
    [InlineData("MigrationChecksumMismatch", false)]
    [InlineData("MigrationHistoryInvalid", false)]
    [InlineData("MigrationLedgerSchemaInvalid", false)]
    [InlineData("MigrationSchemaDrift", false)]
    public void ResultCompatibilityDependsOnlyOnCompatibleClassification(
        string classificationName,
        bool expectedIsCompatible)
    {
        var classification = Enum.Parse<SqlRuntimeCompatibilityClassification>(classificationName);
        var result = new SqlRuntimeCompatibilityResult(classification);

        Assert.Equal(classification, result.Classification);
        Assert.Equal(expectedIsCompatible, result.IsCompatible);
    }

    [Fact]
    public void ExactCurrentHistoryHasNoTerminalCompatibilityClassification()
    {
        var mapped = SqlRuntimeMigrationHistoryCompatibilityMapping.MapTerminal(
            SqlRuntimeMigrationHistoryClassification.ExactCurrent);

        Assert.Null(mapped);
    }

    [Theory]
    [InlineData("ExactPrefixPending", "MigrationPending")]
    [InlineData("DatabaseNewerThanSupported", "DatabaseNewerThanSupported")]
    [InlineData("IdentityMismatch", "MigrationIdentityMismatch")]
    [InlineData("ChecksumMismatch", "MigrationChecksumMismatch")]
    [InlineData("RowSemanticsInvalid", "MigrationHistoryInvalid")]
    public void TerminalHistoryClassificationMappingIsClosedAndExact(
        string historyClassificationName,
        string compatibilityClassificationName)
    {
        var historyClassification = Enum.Parse<SqlRuntimeMigrationHistoryClassification>(historyClassificationName);
        var expected = Enum.Parse<SqlRuntimeCompatibilityClassification>(compatibilityClassificationName);

        var actual = SqlRuntimeMigrationHistoryCompatibilityMapping.MapTerminal(historyClassification);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value);
    }
}
