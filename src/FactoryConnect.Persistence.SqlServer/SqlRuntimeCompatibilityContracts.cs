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
