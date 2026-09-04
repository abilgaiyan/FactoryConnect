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

internal enum SqlRuntimeCompatibilityDiagnosticCode
{
    DatabaseUninitialized,
    LegacyAdoptionRequired,
    UnledgeredSchemaDifference,
    MigrationPending,
    DatabaseNewerThanSupported,
    MigrationIdentityMismatch,
    MigrationChecksumMismatch,
    MigrationHistoryChecksumInvalid,
    MigrationHistoryAppliedAtUtcOffsetInvalid,
    MigrationLedgerObjectKindInvalid,
    MigrationLedgerStructureInvalid,
    MigrationSchemaDifference
}

internal sealed record SqlRuntimeCompatibilityDiagnostic
{
    public SqlRuntimeCompatibilityDiagnostic(
        SqlRuntimeCompatibilityDiagnosticCode code,
        SqlRuntimeCompatibilityDecisionStage stage,
        string artifact,
        string? expected,
        string? actual,
        string detail,
        SqlSchemaDifferenceKind? schemaDifferenceKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        Code = code;
        Stage = stage;
        Artifact = artifact;
        Expected = expected;
        Actual = actual;
        Detail = detail;
        SchemaDifferenceKind = schemaDifferenceKind;
    }

    public SqlRuntimeCompatibilityDiagnosticCode Code { get; }

    public SqlRuntimeCompatibilityDecisionStage Stage { get; }

    public string Artifact { get; }

    public string? Expected { get; }

    public string? Actual { get; }

    public string Detail { get; }

    public SqlSchemaDifferenceKind? SchemaDifferenceKind { get; }
}

internal sealed record SqlRuntimeCompatibilityResult
{
    public SqlRuntimeCompatibilityResult(
        SqlRuntimeCompatibilityClassification classification,
        ImmutableArray<SqlRuntimeCompatibilityDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefault)
        {
            throw new ArgumentException("Runtime compatibility diagnostics must be an initialized immutable array.", nameof(diagnostics));
        }

        if (classification == SqlRuntimeCompatibilityClassification.Compatible)
        {
            if (!diagnostics.IsEmpty)
            {
                throw new ArgumentException("Compatible runtime results must not contain diagnostics.", nameof(diagnostics));
            }
        }
        else if (diagnostics.IsEmpty)
        {
            throw new ArgumentException("Non-compatible runtime results must contain at least one diagnostic.", nameof(diagnostics));
        }

        Classification = classification;
        Diagnostics = diagnostics;
    }

    public SqlRuntimeCompatibilityClassification Classification { get; }

    public ImmutableArray<SqlRuntimeCompatibilityDiagnostic> Diagnostics { get; }

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
