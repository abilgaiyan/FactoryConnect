using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlRuntimeCompatibilityClassification
{
    Compatible,
    DatabaseUninitialized,
    LegacyAdoptionRequired,
    UnledgeredSchemaIncompatible,
    MigrationPending,
    DatabaseNewerThanSupported,
    MigrationIdentityMismatch,
    MigrationChecksumMismatch,
    MigrationHistoryInvalid,
    MigrationLedgerSchemaInvalid,
    MigrationSchemaDrift
}

internal enum SqlRuntimeCompatibilityDecisionStage
{
    LedgerIdentityAndPhysicalShape,
    LedgerRowSemantics,
    HistoryCatalogRelationship,
    UnledgeredSchemaClassification,
    CurrentSchemaComparison
}

internal sealed record SqlRuntimeCompatibilityResult(
    SqlRuntimeCompatibilityClassification Classification)
{
    public bool IsCompatible => Classification == SqlRuntimeCompatibilityClassification.Compatible;
}

internal static class SqlRuntimeCompatibilityDecisionPrecedence
{
    public static ImmutableArray<SqlRuntimeCompatibilityDecisionStage> OrderedStages { get; } =
    [
        SqlRuntimeCompatibilityDecisionStage.LedgerIdentityAndPhysicalShape,
        SqlRuntimeCompatibilityDecisionStage.LedgerRowSemantics,
        SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
        SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification,
        SqlRuntimeCompatibilityDecisionStage.CurrentSchemaComparison
    ];
}

internal static class SqlRuntimeMigrationHistoryCompatibilityMapping
{
    public static SqlRuntimeCompatibilityClassification? MapTerminal(
        SqlRuntimeMigrationHistoryClassification classification) => classification switch
        {
            SqlRuntimeMigrationHistoryClassification.ExactCurrent => null,
            SqlRuntimeMigrationHistoryClassification.ExactPrefixPending => SqlRuntimeCompatibilityClassification.MigrationPending,
            SqlRuntimeMigrationHistoryClassification.DatabaseNewerThanSupported => SqlRuntimeCompatibilityClassification.DatabaseNewerThanSupported,
            SqlRuntimeMigrationHistoryClassification.IdentityMismatch => SqlRuntimeCompatibilityClassification.MigrationIdentityMismatch,
            SqlRuntimeMigrationHistoryClassification.ChecksumMismatch => SqlRuntimeCompatibilityClassification.MigrationChecksumMismatch,
            SqlRuntimeMigrationHistoryClassification.RowSemanticsInvalid => SqlRuntimeCompatibilityClassification.MigrationHistoryInvalid,
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, "Unknown migration history classification.")
        };
}
