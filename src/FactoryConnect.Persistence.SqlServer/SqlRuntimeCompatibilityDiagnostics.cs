using System.Collections.Immutable;
using System.Globalization;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRuntimeCompatibilityDiagnostics
{
    private const string LedgerArtifact = "dbo.FactoryConnectMigrationHistory";

    public static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> DatabaseUninitialized() =>
    [
        new(
            SqlRuntimeCompatibilityDiagnosticCode.DatabaseUninitialized,
            SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification,
            "FactoryConnectOwnedSchema",
            expected: "No migration ledger and no FactoryConnect-owned objects.",
            actual: "No migration ledger and no FactoryConnect-owned objects.",
            detail: "Database has no recognized FactoryConnect-owned schema."),
    ];

    public static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> LegacyAdoptionRequired() =>
    [
        new(
            SqlRuntimeCompatibilityDiagnosticCode.LegacyAdoptionRequired,
            SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification,
            "FactoryConnectOwnedSchema",
            expected: "LegacyPost004SchemaDescriptor",
            actual: "Exact legacy post-004 schema without migration ledger.",
            detail: "Database requires explicit legacy adoption before runtime compatibility can be established."),
    ];

    public static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> UnledgeredSchemaIncompatible(
        SqlSchemaComparisonResult comparison) =>
        ProjectSchemaDifferences(
            comparison,
            SqlRuntimeCompatibilityDiagnosticCode.UnledgeredSchemaDifference,
            SqlRuntimeCompatibilityDecisionStage.UnledgeredSchemaClassification);

    public static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> IncompatibleLedgerObject(
        string? catalogObjectType) =>
    [
        new(
            SqlRuntimeCompatibilityDiagnosticCode.MigrationLedgerObjectKindInvalid,
            SqlRuntimeCompatibilityDecisionStage.LedgerIdentityAndPhysicalShape,
            LedgerArtifact,
            expected: "User table",
            actual: catalogObjectType ?? "<unknown>",
            detail: "Migration ledger identity is occupied by an incompatible SQL object type."),
    ];

    public static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> InvalidLedgerStructure(
        SqlMigrationLedgerSchemaException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return
        [
            new(
                SqlRuntimeCompatibilityDiagnosticCode.MigrationLedgerStructureInvalid,
                SqlRuntimeCompatibilityDecisionStage.LedgerIdentityAndPhysicalShape,
                LedgerArtifact,
                expected: "Exact FactoryConnect migration ledger contract",
                actual: null,
                detail: exception.Message),
        ];
    }

    public static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> ForHistory(
        SqlRuntimeMigrationHistoryClassification classification,
        ImmutableArray<SqlMigrationHistoryRow> history,
        SqlMigrationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return classification switch
        {
            SqlRuntimeMigrationHistoryClassification.ExactPrefixPending => Pending(history, catalog),
            SqlRuntimeMigrationHistoryClassification.DatabaseNewerThanSupported => NewerThanSupported(history, catalog),
            SqlRuntimeMigrationHistoryClassification.IdentityMismatch => IdentityMismatch(history, catalog),
            SqlRuntimeMigrationHistoryClassification.ChecksumMismatch => ChecksumMismatch(history, catalog),
            SqlRuntimeMigrationHistoryClassification.RowSemanticsInvalid => RowSemanticsInvalid(history),
            SqlRuntimeMigrationHistoryClassification.ExactCurrent => throw new ArgumentException(
                "Exact current history is non-terminal and has no terminal history diagnostic.",
                nameof(classification)),
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, "Unknown migration history classification."),
        };
    }

    public static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> SchemaDrift(
        SqlSchemaComparisonResult comparison) =>
        ProjectSchemaDifferences(
            comparison,
            SqlRuntimeCompatibilityDiagnosticCode.MigrationSchemaDifference,
            SqlRuntimeCompatibilityDecisionStage.CurrentSchemaComparison);

    private static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> Pending(
        ImmutableArray<SqlMigrationHistoryRow> history,
        SqlMigrationCatalog catalog)
    {
        var next = catalog.Migrations[history.Length];
        return
        [
            new(
                SqlRuntimeCompatibilityDiagnosticCode.MigrationPending,
                SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
                MigrationArtifact(next.MigrationId),
                expected: Identity(next.MigrationId, next.Name),
                actual: "<not-applied>",
                detail: "Repository migration is not present in the exact applied history prefix."),
        ];
    }

    private static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> NewerThanSupported(
        ImmutableArray<SqlMigrationHistoryRow> history,
        SqlMigrationCatalog catalog)
    {
        var row = history[catalog.Migrations.Length];
        return
        [
            new(
                SqlRuntimeCompatibilityDiagnosticCode.DatabaseNewerThanSupported,
                SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
                MigrationArtifact(row.MigrationId),
                expected: "No migration beyond the supported repository catalog.",
                actual: Identity(row.MigrationId, row.Name),
                detail: "Database contains a valid migration beyond this binary's supported catalog."),
        ];
    }

    private static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> IdentityMismatch(
        ImmutableArray<SqlMigrationHistoryRow> history,
        SqlMigrationCatalog catalog)
    {
        var overlappingCount = Math.Min(history.Length, catalog.Migrations.Length);
        for (var index = 0; index < overlappingCount; index++)
        {
            var row = history[index];
            var descriptor = catalog.Migrations[index];
            if (row.MigrationId != descriptor.MigrationId ||
                !string.Equals(row.Name, descriptor.Name, StringComparison.Ordinal))
            {
                return SingleIdentityMismatch(
                    row,
                    expected: Identity(descriptor.MigrationId, descriptor.Name),
                    detail: "Applied migration identity does not match repository authority at the same history position.");
            }
        }

        var knownNames = new HashSet<string>(
            catalog.Migrations.Select(static migration => migration.Name),
            StringComparer.Ordinal);
        var previousMigrationId = catalog.Migrations[^1].MigrationId;
        for (var index = catalog.Migrations.Length; index < history.Length; index++)
        {
            var row = history[index];
            string? detail = null;
            if (row.MigrationId <= previousMigrationId)
            {
                detail = "Future migration id is not strictly increasing.";
            }
            else if (!SqlMigrationCatalog.IsValidMigrationName(row.Name))
            {
                detail = "Future migration name does not satisfy the frozen migration-name grammar.";
            }
            else if (!knownNames.Add(row.Name))
            {
                detail = "Future migration name duplicates a supported or earlier future migration name.";
            }

            if (detail is not null)
            {
                return SingleIdentityMismatch(
                    row,
                    expected: "Strictly increasing id and unique valid migration name",
                    detail);
            }

            previousMigrationId = row.MigrationId;
        }

        throw new InvalidOperationException("IdentityMismatch classification has no corresponding deterministic identity evidence.");
    }

    private static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> SingleIdentityMismatch(
        SqlMigrationHistoryRow row,
        string expected,
        string detail) =>
    [
        new(
            SqlRuntimeCompatibilityDiagnosticCode.MigrationIdentityMismatch,
            SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
            MigrationArtifact(row.MigrationId),
            expected,
            Identity(row.MigrationId, row.Name),
            detail),
    ];

    private static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> ChecksumMismatch(
        ImmutableArray<SqlMigrationHistoryRow> history,
        SqlMigrationCatalog catalog)
    {
        var overlappingCount = Math.Min(history.Length, catalog.Migrations.Length);
        for (var index = 0; index < overlappingCount; index++)
        {
            var row = history[index];
            var descriptor = catalog.Migrations[index];
            if (row.MigrationId == descriptor.MigrationId &&
                string.Equals(row.Name, descriptor.Name, StringComparison.Ordinal) &&
                !string.Equals(row.CanonicalChecksum, descriptor.Sha256Checksum, StringComparison.Ordinal))
            {
                return
                [
                    new(
                        SqlRuntimeCompatibilityDiagnosticCode.MigrationChecksumMismatch,
                        SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship,
                        MigrationArtifact(row.MigrationId),
                        descriptor.Sha256Checksum,
                        row.CanonicalChecksum,
                        "Applied migration checksum differs from repository authority."),
                ];
            }
        }

        throw new InvalidOperationException("ChecksumMismatch classification has no corresponding deterministic checksum evidence.");
    }

    private static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> RowSemanticsInvalid(
        ImmutableArray<SqlMigrationHistoryRow> history)
    {
        foreach (var row in history)
        {
            if (!SqlMigrationHistoryRowValidator.IsCanonicalChecksum(row.CanonicalChecksum))
            {
                return
                [
                    new(
                        SqlRuntimeCompatibilityDiagnosticCode.MigrationHistoryChecksumInvalid,
                        SqlRuntimeCompatibilityDecisionStage.LedgerRowSemantics,
                        MigrationArtifact(row.MigrationId),
                        "64 uppercase hexadecimal characters",
                        row.CanonicalChecksum,
                        "Applied migration checksum is not in canonical ledger form."),
                ];
            }

            if (row.AppliedAtUtc.Offset != TimeSpan.Zero)
            {
                return
                [
                    new(
                        SqlRuntimeCompatibilityDiagnosticCode.MigrationHistoryAppliedAtUtcOffsetInvalid,
                        SqlRuntimeCompatibilityDecisionStage.LedgerRowSemantics,
                        MigrationArtifact(row.MigrationId),
                        TimeSpan.Zero.ToString("c", CultureInfo.InvariantCulture),
                        row.AppliedAtUtc.Offset.ToString("c", CultureInfo.InvariantCulture),
                        "AppliedAtUtc must have UTC offset zero."),
                ];
            }
        }

        throw new InvalidOperationException("RowSemanticsInvalid classification has no corresponding deterministic row-semantic evidence.");
    }

    private static ImmutableArray<SqlRuntimeCompatibilityDiagnostic> ProjectSchemaDifferences(
        SqlSchemaComparisonResult comparison,
        SqlRuntimeCompatibilityDiagnosticCode code,
        SqlRuntimeCompatibilityDecisionStage stage)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        return comparison.Differences
            .Select(difference => new SqlRuntimeCompatibilityDiagnostic(
                code,
                stage,
                $"{difference.Table.SchemaName}.{difference.Table.ObjectName}:{difference.ArtifactName}",
                expected: null,
                actual: null,
                detail: difference.Detail,
                SchemaDifferenceKind: difference.Kind))
            .ToImmutableArray();
    }

    private static string MigrationArtifact(int migrationId) =>
        $"{LedgerArtifact}:MigrationId={migrationId.ToString(CultureInfo.InvariantCulture)}";

    private static string Identity(int migrationId, string name) =>
        $"{migrationId.ToString(CultureInfo.InvariantCulture)}:{name}";
}
