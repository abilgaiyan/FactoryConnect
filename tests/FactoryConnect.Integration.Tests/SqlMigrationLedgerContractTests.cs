using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigrationLedgerContractTests
{
    [Fact]
    public void FrozenLedgerContractHasExactColumnsAndPrimaryKey()
    {
        Assert.Collection(
            SqlMigrationLedgerContract.Columns,
            column => Assert.Equal("MigrationId", column.Name),
            column => Assert.Equal("Name", column.Name),
            column => Assert.Equal("CanonicalChecksum", column.Name),
            column => Assert.Equal("AppliedAtUtc", column.Name));
        Assert.Equal("PK_FactoryConnectMigrationHistory", SqlMigrationLedgerContract.PrimaryKey.Name);
        Assert.True(SqlMigrationLedgerContract.PrimaryKey.IsClustered);
        Assert.True(SqlMigrationLedgerContract.PrimaryKey.IsEnabled);
        var key = Assert.Single(SqlMigrationLedgerContract.PrimaryKey.KeyColumns);
        Assert.Equal("MigrationId", key.Name);
        Assert.Equal(1, key.Ordinal);
        Assert.Equal(SqlIndexColumnDirection.Ascending, key.Direction);
    }

    [Theory]
    [InlineData("E1C14282B7A246BBD9D5734370498695721D3F0A78D60F74531E35D5FEDC9057", true)]
    [InlineData("e1C14282B7A246BBD9D5734370498695721D3F0A78D60F74531E35D5FEDC9057", false)]
    [InlineData("G1C14282B7A246BBD9D5734370498695721D3F0A78D60F74531E35D5FEDC9057", false)]
    [InlineData("E1C14282B7A246BBD9D5734370498695721D3F0A78D60F74531E35D5FEDC905", false)]
    public void CanonicalChecksumValidationIsExact(string checksum, bool expected)
    {
        Assert.Equal(expected, SqlMigrationHistoryRowValidator.IsCanonicalChecksum(checksum));
    }

    [Fact]
    public void HistoryRowRejectsNonZeroUtcOffsetWithHistoryFailure()
    {
        var row = new SqlMigrationHistoryRow(
            1,
            "InitialObservationIngestion",
            "E1C14282B7A246BBD9D5734370498695721D3F0A78D60F74531E35D5FEDC9057",
            new DateTimeOffset(2026, 9, 4, 5, 0, 0, TimeSpan.FromHours(5.5)));

        var exception = Assert.Throws<SqlMigrationHistoryException>(
            () => SqlMigrationHistoryRowValidator.Validate(row));

        Assert.Contains("UTC offset zero", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactCatalogPrefixReturnsPendingStartIndex()
    {
        var catalog = SqlMigrationCatalog.Load();
        var history = catalog.Migrations
            .Take(2)
            .Select(static migration => new SqlMigrationHistoryRow(
                migration.MigrationId,
                migration.Name,
                migration.Sha256Checksum,
                new DateTimeOffset(2026, 9, 4, 5, 0, 0, TimeSpan.Zero)))
            .ToImmutableArray();

        Assert.Equal(2, SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(history, catalog));
    }

    [Fact]
    public void ChecksumCasingDifferenceIsHistoryFailure()
    {
        var catalog = SqlMigrationCatalog.Load();
        var migration = catalog.Migrations[0];
        var history = ImmutableArray.Create(new SqlMigrationHistoryRow(
            migration.MigrationId,
            migration.Name,
            migration.Sha256Checksum.ToLowerInvariant(),
            new DateTimeOffset(2026, 9, 4, 5, 0, 0, TimeSpan.Zero)));

        Assert.Throws<SqlMigrationHistoryException>(
            () => SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(history, catalog));
    }

    [Fact]
    public void LedgerSchemaValidatorComparesPrimaryKeyStructurally()
    {
        var independentlyConstructedPrimaryKey = new SqlMigrationLedgerPrimaryKeyDescriptor(
            SqlMigrationLedgerContract.PrimaryKeyName,
            IsClustered: true,
            IsEnabled: true,
            ImmutableArray.Create(new SqlIndexColumnDescriptor(
                "MigrationId",
                SqlIndexColumnDirection.Ascending,
                1)));
        var snapshot = ExactSnapshot() with { PrimaryKey = independentlyConstructedPrimaryKey };

        SqlMigrationLedgerSchemaValidator.Validate(snapshot);
    }

    [Fact]
    public void LedgerSchemaValidatorIgnoresPhysicalColumnOrder()
    {
        var reorderedColumns = SqlMigrationLedgerContract.Columns.Reverse().ToImmutableArray();
        var snapshot = ExactSnapshot() with { Columns = reorderedColumns };

        SqlMigrationLedgerSchemaValidator.Validate(snapshot);
    }

    [Fact]
    public void LedgerSchemaValidatorRejectsEveryForbiddenArtifactCategoryWithSchemaFailure()
    {
        var exact = ExactSnapshot();
        SqlMigrationLedgerSchemaValidator.Validate(exact);

        AssertInvalid(exact with { UniqueConstraints = ["UQ_Unexpected"] });
        AssertInvalid(exact with { ForeignKeys = ["FK_Unexpected"] });
        AssertInvalid(exact with { DefaultConstraints = ["DF_Unexpected"] });
        AssertInvalid(exact with { CheckConstraints = ["CK_Unexpected"] });
        AssertInvalid(exact with { OrdinaryIndexes = ["IX_Unexpected"] });
        AssertInvalid(exact with { Triggers = ["TR_Unexpected"] });
    }

    private static SqlMigrationLedgerSchemaSnapshot ExactSnapshot() => new(
        SqlMigrationLedgerContract.Columns,
        SqlMigrationLedgerContract.PrimaryKey,
        [],
        [],
        [],
        [],
        [],
        []);

    private static void AssertInvalid(SqlMigrationLedgerSchemaSnapshot snapshot) =>
        Assert.Throws<SqlMigrationLedgerSchemaException>(
            () => SqlMigrationLedgerSchemaValidator.Validate(snapshot));
}
