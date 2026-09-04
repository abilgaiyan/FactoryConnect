using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlRuntimeMigrationHistoryClassifierTests
{
    private static readonly DateTimeOffset AppliedAtUtc =
        new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyHistoryIsExactPendingPrefix()
    {
        var result = SqlRuntimeMigrationHistoryClassifier.Classify([], SqlMigrationCatalog.Load());

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.ExactPrefixPending, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ProperCatalogPrefixIsPending(int appliedCount)
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, appliedCount);

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history, catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.ExactPrefixPending, result);
    }

    [Fact]
    public void CompleteExactHistoryIsCurrent()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length);

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history, catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.ExactCurrent, result);
    }

    [Fact]
    public void WrongNameForKnownMigrationIsIdentityMismatch()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[1] = history[1] with { Name = "WrongName" };

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history.ToImmutable(), catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.IdentityMismatch, result);
    }

    [Fact]
    public void WrongIdAtCatalogPositionIsIdentityMismatch()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[1] = history[1] with { MigrationId = 99 };

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history.ToImmutable(), catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.IdentityMismatch, result);
    }

    [Fact]
    public void CanonicalButWrongChecksumIsChecksumMismatch()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[2] = history[2] with { CanonicalChecksum = new string('A', 64) };

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history.ToImmutable(), catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.ChecksumMismatch, result);
    }

    [Theory]
    [InlineData("lowercase-checksum")]
    [InlineData("non-zero-offset")]
    public void MalformedLedgerRowSemanticsPrecedeCatalogRelationship(string defect)
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[0] = defect switch
        {
            "lowercase-checksum" => history[0] with
            {
                CanonicalChecksum = history[0].CanonicalChecksum.ToLowerInvariant()
            },
            "non-zero-offset" => history[0] with
            {
                AppliedAtUtc = new DateTimeOffset(2026, 9, 4, 17, 30, 0, TimeSpan.FromHours(5.5))
            },
            _ => throw new InvalidOperationException("Unknown test defect.")
        };

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history.ToImmutable(), catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.RowSemanticsInvalid, result);
    }

    [Fact]
    public void ExactSupportedHistoryFollowedByWellFormedFutureRowsIsNewerThanSupported()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length)
            .Add(new SqlMigrationHistoryRow(
                5,
                "SyntheticFutureMigration",
                new string('A', 64),
                AppliedAtUtc));

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history, catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.DatabaseNewerThanSupported, result);
    }

    [Fact]
    public void LongerHistoryDoesNotHideSupportedPrefixIdentityMismatch()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length).ToBuilder();
        history[0] = history[0] with { Name = "WrongName" };
        history.Add(new SqlMigrationHistoryRow(
            5,
            "SyntheticFutureMigration",
            new string('A', 64),
            AppliedAtUtc));

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history.ToImmutable(), catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.IdentityMismatch, result);
    }

    [Theory]
    [InlineData(4, "DuplicateSupportedId")]
    [InlineData(3, "ReorderedFutureId")]
    [InlineData(5, " ")]
    public void InvalidFutureIdentityDoesNotQualifyAsNewerThanSupported(
        int migrationId,
        string name)
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = CreateExactHistory(catalog, catalog.Migrations.Length)
            .Add(new SqlMigrationHistoryRow(
                migrationId,
                name,
                new string('A', 64),
                AppliedAtUtc));

        var result = SqlRuntimeMigrationHistoryClassifier.Classify(history, catalog);

        Assert.Equal(SqlRuntimeMigrationHistoryClassification.IdentityMismatch, result);
    }

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
