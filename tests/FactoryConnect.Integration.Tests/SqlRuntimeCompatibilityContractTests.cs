using System.Collections.Immutable;
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

    private static readonly SqlRuntimeCompatibilityDiagnosticCode[] ExpectedDiagnosticCodes =
    [
        SqlRuntimeCompatibilityDiagnosticCode.DatabaseUninitialized,
        SqlRuntimeCompatibilityDiagnosticCode.LegacyAdoptionRequired,
        SqlRuntimeCompatibilityDiagnosticCode.UnledgeredSchemaDifference,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationPending,
        SqlRuntimeCompatibilityDiagnosticCode.DatabaseNewerThanSupported,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationIdentityMismatch,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationChecksumMismatch,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationHistoryChecksumInvalid,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationHistoryAppliedAtUtcOffsetInvalid,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationLedgerObjectKindInvalid,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationLedgerStructureInvalid,
        SqlRuntimeCompatibilityDiagnosticCode.MigrationSchemaDifference
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

    [Fact]
    public void DiagnosticVocabularyIsClosedAndOrdered()
    {
        Assert.Equal(ExpectedDiagnosticCodes, Enum.GetValues<SqlRuntimeCompatibilityDiagnosticCode>());
    }

    [Fact]
    public void CompatibleResultRequiresEmptyDiagnostics()
    {
        var result = new SqlRuntimeCompatibilityResult(
            SqlRuntimeCompatibilityClassification.Compatible,
            ImmutableArray<SqlRuntimeCompatibilityDiagnostic>.Empty);

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CompatibleResultRejectsDiagnostics()
    {
        var diagnostics = ImmutableArray.Create(CreateDiagnostic());

        Assert.Throws<ArgumentException>(() => new SqlRuntimeCompatibilityResult(
            SqlRuntimeCompatibilityClassification.Compatible,
            diagnostics));
    }

    [Fact]
    public void NonCompatibleResultRequiresDiagnostics()
    {
        Assert.Throws<ArgumentException>(() => new SqlRuntimeCompatibilityResult(
            SqlRuntimeCompatibilityClassification.DatabaseUninitialized,
            ImmutableArray<SqlRuntimeCompatibilityDiagnostic>.Empty));
    }

    [Fact]
    public void ResultRejectsDefaultDiagnosticArray()
    {
        Assert.Throws<ArgumentException>(() => new SqlRuntimeCompatibilityResult(
            SqlRuntimeCompatibilityClassification.DatabaseUninitialized,
            default));
    }

    [Theory]
    [InlineData("DatabaseUninitialized")]
    [InlineData("LegacyAdoptionRequired")]
    [InlineData("UnledgeredSchemaIncompatible")]
    [InlineData("MigrationPending")]
    [InlineData("DatabaseNewerThanSupported")]
    [InlineData("MigrationIdentityMismatch")]
    [InlineData("MigrationChecksumMismatch")]
    [InlineData("MigrationHistoryInvalid")]
    [InlineData("MigrationLedgerSchemaInvalid")]
    [InlineData("MigrationSchemaDrift")]
    public void NonCompatibleClassificationRemainsNotCompatible(string classificationName)
    {
        var classification = Enum.Parse<SqlRuntimeCompatibilityClassification>(classificationName);
        var result = new SqlRuntimeCompatibilityResult(
            classification,
            ImmutableArray.Create(CreateDiagnostic()));

        Assert.Equal(classification, result.Classification);
        Assert.False(result.IsCompatible);
    }

    [Fact]
    public void DiagnosticRejectsBlankArtifact()
    {
        Assert.Throws<ArgumentException>(() => new SqlRuntimeCompatibilityDiagnostic(
            SqlRuntimeCompatibilityDiagnosticCode.DatabaseUninitialized,
            SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification,
            " ",
            expected: null,
            actual: null,
            detail: "detail"));
    }

    [Fact]
    public void DiagnosticRejectsBlankDetail()
    {
        Assert.Throws<ArgumentException>(() => new SqlRuntimeCompatibilityDiagnostic(
            SqlRuntimeCompatibilityDiagnosticCode.DatabaseUninitialized,
            SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification,
            "FactoryConnectOwnedSchema",
            expected: null,
            actual: null,
            detail: " "));
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

    private static SqlRuntimeCompatibilityDiagnostic CreateDiagnostic() =>
        new(
            SqlRuntimeCompatibilityDiagnosticCode.DatabaseUninitialized,
            SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification,
            "FactoryConnectOwnedSchema",
            expected: null,
            actual: null,
            detail: "Diagnostic evidence.");
}
