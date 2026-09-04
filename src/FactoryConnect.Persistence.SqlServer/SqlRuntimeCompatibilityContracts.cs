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
    public static bool TryMapTerminal(
        SqlRuntimeMigrationHistoryClassification classification,
        out SqlRuntimeCompatibilityClassification compatibilityClassification)
    {
        switch (classification)
        {
            case SqlRuntimeMigrationHistoryClassification.ExactCurrent:
                compatibilityClassification = default;
                return false;
            case SqlRuntimeMigrationHistoryClassification.ExactPrefixPending:
                compatibilityClassification = SqlRuntimeCompatibilityClassification.MigrationPending;
                return true;
            case SqlRuntimeMigrationHistoryClassification.DatabaseNewerThanSupported:
                compatibilityClassification = SqlRuntimeCompatibilityClassification.DatabaseNewerThanSupported;
                return true;
            case SqlRuntimeMigrationHistoryClassification.IdentityMismatch:
                compatibilityClassification = SqlRuntimeCompatibilityClassification.MigrationIdentityMismatch;
                return true;
            case SqlRuntimeMigrationHistoryClassification.ChecksumMismatch:
                compatibilityClassification = SqlRuntimeCompatibilityClassification.MigrationChecksumMismatch;
                return true;
            case SqlRuntimeMigrationHistoryClassification.RowSemanticsInvalid:
                compatibilityClassification = SqlRuntimeCompatibilityClassification.MigrationHistoryInvalid;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(classification), classification, "Unknown migration history classification.");
        }
    }
}
