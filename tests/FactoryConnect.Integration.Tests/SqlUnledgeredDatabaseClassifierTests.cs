using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlUnledgeredDatabaseClassifierTests
{
    [Fact]
    public void EmptyOwnedSnapshotIsUninitialized()
    {
        var snapshot = new SqlSchemaDescriptor([]);

        var classification = SqlUnledgeredDatabaseClassifier.Classify(snapshot);

        Assert.Equal(UnledgeredDatabaseClassification.Uninitialized, classification);
    }

    [Fact]
    public void ExactLegacyPost004SnapshotIsLegacyAdoptable()
    {
        var classification = SqlUnledgeredDatabaseClassifier.Classify(
            SqlRepositorySchemaDescriptors.LegacyPost004);

        Assert.Equal(UnledgeredDatabaseClassification.LegacyAdoptable, classification);
    }

    [Fact]
    public void ExactLegacySnapshotRemainsAdoptableWhenInputCollectionsAreReordered()
    {
        var legacy = SqlRepositorySchemaDescriptors.LegacyPost004;
        var snapshot = legacy with
        {
            Tables = legacy.Tables
                .Reverse()
                .Select(static table => table with
                {
                    Columns = table.Columns.Reverse().ToImmutableArray(),
                    UniqueConstraints = table.UniqueConstraints.Reverse().ToImmutableArray(),
                    ForeignKeys = table.ForeignKeys.Reverse().ToImmutableArray(),
                    CheckConstraints = table.CheckConstraints.Reverse().ToImmutableArray(),
                    Indexes = table.Indexes.Reverse().ToImmutableArray()
                })
                .ToImmutableArray()
        };

        var classification = SqlUnledgeredDatabaseClassifier.Classify(snapshot);

        Assert.Equal(UnledgeredDatabaseClassification.LegacyAdoptable, classification);
    }

    [Fact]
    public void RecognizablePartialLegacySnapshotIsPartialOrIncompatibleLegacy()
    {
        var legacy = SqlRepositorySchemaDescriptors.LegacyPost004;
        var snapshot = new SqlSchemaDescriptor([legacy.Tables[0]]);

        var classification = SqlUnledgeredDatabaseClassifier.Classify(snapshot);

        Assert.Equal(UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy, classification);
    }

    [Fact]
    public void RecognizableStructurallyDriftedSnapshotIsPartialOrIncompatibleLegacy()
    {
        var legacy = SqlRepositorySchemaDescriptors.LegacyPost004;
        var table = legacy.Tables[0];
        var column = table.Columns[0];
        var snapshot = legacy with
        {
            Tables = legacy.Tables.SetItem(
                0,
                table with
                {
                    Columns = table.Columns.SetItem(
                        0,
                        column with { IsNullable = !column.IsNullable })
                })
        };

        var classification = SqlUnledgeredDatabaseClassifier.Classify(snapshot);

        Assert.Equal(UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy, classification);
    }

    [Fact]
    public void SuppliedOwnedSnapshotWithUnexpectedTableIsPartialOrIncompatibleLegacy()
    {
        var legacy = SqlRepositorySchemaDescriptors.LegacyPost004;
        var unexpected = legacy.Tables[0] with
        {
            Name = new SqlObjectName("dbo", "UnexpectedFactoryConnectTable")
        };
        var snapshot = legacy with
        {
            Tables = legacy.Tables.Add(unexpected)
        };

        var classification = SqlUnledgeredDatabaseClassifier.Classify(snapshot);

        Assert.Equal(UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy, classification);
    }

    [Fact]
    public void NullSnapshotIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            static () => SqlUnledgeredDatabaseClassifier.Classify(null!));
    }
}
