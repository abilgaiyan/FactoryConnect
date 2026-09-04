using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlRuntimeCompatibilityDiagnosticTests
{
    private static readonly DateTimeOffset AppliedAtUtc =
        new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PendingHistoryIdentifiesFirstMissingRepositoryMigration()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, 2);

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.ExactPrefixPending,
            history,
            catalog);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationPending, diagnostic.Code);
        Assert.Equal(SqlRuntimeCompatibilityDecisionStage.HistoryCatalogRelationship, diagnostic.Stage);
        Assert.Contains("MigrationId=3", diagnostic.Artifact, StringComparison.Ordinal);
        Assert.Equal($"3:{catalog.Migrations[2].Name}", diagnostic.Expected);
        Assert.Equal("<not-applied>", diagnostic.Actual);
    }

    [Fact]
    public void NewerHistoryIdentifiesFirstUnsupportedMigration()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length)
            .Add(CreateFutureRow(5, "SyntheticFutureMigration"))
            .Add(CreateFutureRow(6, "SyntheticFutureMigration006"));

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.DatabaseNewerThanSupported,
            history,
            catalog);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.DatabaseNewerThanSupported, diagnostic.Code);
        Assert.Contains("MigrationId=5", diagnostic.Artifact, StringComparison.Ordinal);
        Assert.Equal("5:SyntheticFutureMigration", diagnostic.Actual);
    }

    [Fact]
    public void KnownIdentityMismatchIdentifiesExpectedAndActualIdentity()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        var expectedName = catalog.Migrations[1].Name;
        history[1] = history[1] with { Name = "WrongName" };

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.IdentityMismatch,
            history.ToImmutable(),
            catalog);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationIdentityMismatch, diagnostic.Code);
        Assert.Equal($"2:{expectedName}", diagnostic.Expected);
        Assert.Equal("2:WrongName", diagnostic.Actual);
    }

    [Fact]
    public void FutureIdentityMismatchReportsFirstInvalidFutureRow()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length)
            .Add(CreateFutureRow(5, "SyntheticFutureMigration"))
            .Add(CreateFutureRow(6, catalog.Migrations[0].Name));

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.IdentityMismatch,
            history,
            catalog);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("MigrationId=6", diagnostic.Artifact, StringComparison.Ordinal);
        Assert.Contains("duplicates", diagnostic.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksumMismatchRetainsCanonicalExpectedAndActualValues()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[2] = history[2] with { CanonicalChecksum = new string('A', 64) };

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.ChecksumMismatch,
            history.ToImmutable(),
            catalog);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationChecksumMismatch, diagnostic.Code);
        Assert.Equal(catalog.Migrations[2].Sha256Checksum, diagnostic.Expected);
        Assert.Equal(new string('A', 64), diagnostic.Actual);
    }

    [Fact]
    public void InvalidChecksumPrecedesInvalidUtcOffsetWithinSameRow()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[0] = history[0] with
        {
            CanonicalChecksum = history[0].CanonicalChecksum.ToLowerInvariant(),
            AppliedAtUtc = new DateTimeOffset(2026, 9, 4, 17, 30, 0, TimeSpan.FromHours(5.5)),
        };

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.RowSemanticsInvalid,
            history.ToImmutable(),
            catalog);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationHistoryChecksumInvalid, diagnostic.Code);
        Assert.Equal(SqlRuntimeCompatibilityDecisionStage.LedgerRowSemantics, diagnostic.Stage);
    }

    [Fact]
    public void FirstInvalidHistoryRowPrecedesLaterInvalidRow()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[0] = history[0] with
        {
            AppliedAtUtc = new DateTimeOffset(2026, 9, 4, 13, 0, 0, TimeSpan.FromHours(1)),
        };
        history[1] = history[1] with
        {
            CanonicalChecksum = history[1].CanonicalChecksum.ToLowerInvariant(),
        };

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.RowSemanticsInvalid,
            history.ToImmutable(),
            catalog);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationHistoryAppliedAtUtcOffsetInvalid, diagnostic.Code);
        Assert.Contains("MigrationId=1", diagnostic.Artifact, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactCurrentHistoryCannotProduceTerminalHistoryDiagnostic()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length);

        Assert.Throws<ArgumentException>(() => SqlRuntimeCompatibilityDiagnostics.ForHistory(
            SqlRuntimeMigrationHistoryClassification.ExactCurrent,
            history,
            catalog));
    }

    [Fact]
    public void SchemaDiagnosticProjectionPreservesComparatorDifferenceOrderAndKinds()
    {
        var table = new SqlObjectName("dbo", "Example");
        var comparison = new SqlSchemaComparisonResult(
        [
            new SqlSchemaDifference(
                SqlSchemaDifferenceKind.MissingColumn,
                table,
                "A",
                "Column is missing."),
            new SqlSchemaDifference(
                SqlSchemaDifferenceKind.UnexpectedIndex,
                table,
                "IX_B",
                "Unexpected index is present."),
        ]);

        var diagnostics = SqlRuntimeCompatibilityDiagnostics.SchemaDrift(comparison);

        Assert.Equal(2, diagnostics.Length);
        Assert.Equal(SqlSchemaDifferenceKind.MissingColumn, diagnostics[0].SchemaDifferenceKind);
        Assert.Equal("dbo.Example:A", diagnostics[0].Artifact);
        Assert.Equal(SqlSchemaDifferenceKind.UnexpectedIndex, diagnostics[1].SchemaDifferenceKind);
        Assert.Equal("dbo.Example:IX_B", diagnostics[1].Artifact);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(SqlRuntimeCompatibilityDiagnosticCode.MigrationSchemaDifference, diagnostic.Code);
            Assert.Equal(SqlRuntimeCompatibilityDecisionStage.CurrentSchemaComparison, diagnostic.Stage);
        });
    }

    private static SqlMigrationHistoryRow CreateFutureRow(int migrationId, string name) =>
        new(
            migrationId,
            name,
            new string('A', 64),
            AppliedAtUtc);

    private static ImmutableArray<SqlMigrationHistoryRow> CreateExactHistory(
        SqlMigrationCatalog catalog,
        int appliedCount) =>
        catalog.Migrations
            .Take(appliedCount)
            .Select(static migration => new SqlMigrationHistoryRow(
                migration.MigrationId,
                migration.Name,
                migration.Sha256Checksum,
                AppliedAtUtc))
            .ToImmutableArray();
}
