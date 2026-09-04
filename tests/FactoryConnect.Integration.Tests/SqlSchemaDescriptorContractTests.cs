using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlSchemaDescriptorContractTests
{
    [Fact]
    public void ColumnDescriptorRetainsIdentitySemantics()
    {
        var identity = new SqlIdentityDescriptor(1m, 2m, IsNotForReplication: true);
        var column = new SqlColumnDescriptor(
            "RowId",
            "bigint",
            MaxLength: SqlLengthDescriptor.Bounded(8),
            Precision: 19,
            Scale: 0,
            IsNullable: false,
            Collation: null,
            identity);

        var retainedIdentity = Assert.IsType<SqlIdentityDescriptor>(column.Identity);
        Assert.Same(identity, retainedIdentity);
        Assert.Equal(1m, retainedIdentity.SeedValue);
        Assert.Equal(2m, retainedIdentity.IncrementValue);
        Assert.True(retainedIdentity.IsNotForReplication);
    }

    [Fact]
    public void LengthDescriptorDistinguishesBoundedMaxAndNotApplicable()
    {
        var boundedLength = SqlLengthDescriptor.Bounded(256);
        var maxLength = SqlLengthDescriptor.Max;
        SqlLengthDescriptor? notApplicable = null;

        Assert.Equal(256, boundedLength.Value);
        Assert.False(boundedLength.IsMax);
        Assert.Null(maxLength.Value);
        Assert.True(maxLength.IsMax);
        Assert.Null(notApplicable);
    }

    [Fact]
    public void ForeignKeyDescriptorRetainsOperationalAndReferentialSemantics()
    {
        var foreignKey = new SqlForeignKeyDescriptor(
            "FK_Child_Parent",
            ["ParentId", "MachineId"],
            new SqlObjectName("dbo", "Parent"),
            ["ParentId", "MachineId"],
            SqlReferentialAction.Cascade,
            SqlReferentialAction.SetNull,
            IsEnabled: false,
            IsTrusted: false,
            IsNotForReplication: true);

        Assert.Equal(SqlReferentialAction.Cascade, foreignKey.DeleteAction);
        Assert.Equal(SqlReferentialAction.SetNull, foreignKey.UpdateAction);
        Assert.False(foreignKey.IsEnabled);
        Assert.False(foreignKey.IsTrusted);
        Assert.True(foreignKey.IsNotForReplication);
        Assert.Equal<string>(["ParentId", "MachineId"], foreignKey.Columns);
        Assert.Equal<string>(["ParentId", "MachineId"], foreignKey.ReferencedColumns);
    }

    [Fact]
    public void CheckConstraintDescriptorRetainsOperationalState()
    {
        var constraint = new SqlCheckConstraintDescriptor(
            "CK_Value",
            "([Value]>(0))",
            IsEnabled: false,
            IsTrusted: false,
            IsNotForReplication: true);

        Assert.False(constraint.IsEnabled);
        Assert.False(constraint.IsTrusted);
        Assert.True(constraint.IsNotForReplication);
    }

    [Fact]
    public void IndexStructureRetainsPhysicalAndOrderedColumnSemantics()
    {
        var structure = new SqlIndexStructureDescriptor(
            IsClustered: false,
            KeyColumns:
            [
                new SqlIndexColumnDescriptor("MachineId", SqlIndexColumnDirection.Ascending, 1),
                new SqlIndexColumnDescriptor("Position", SqlIndexColumnDirection.Descending, 2)
            ],
            IncludedColumns: ["MetricValue", "Unit"],
            CanonicalFilterDefinition: "[MetricValue] IS NOT NULL");
        var index = new SqlIndexDescriptor(
            "IX_Test",
            IsUnique: true,
            IsEnabled: false,
            structure);

        Assert.False(index.IsEnabled);
        Assert.True(index.IsUnique);
        Assert.False(index.IndexStructure.IsClustered);
        Assert.Equal(SqlIndexColumnDirection.Ascending, index.IndexStructure.KeyColumns[0].Direction);
        Assert.Equal(SqlIndexColumnDirection.Descending, index.IndexStructure.KeyColumns[1].Direction);
        Assert.Equal<string>(["MetricValue", "Unit"], index.IndexStructure.IncludedColumns);
        Assert.Equal("[MetricValue] IS NOT NULL", index.IndexStructure.CanonicalFilterDefinition);
    }

    [Fact]
    public void RecognitionSetIsIndependentOrderedAndDuplicateFree()
    {
        var set = new SqlOwnedObjectRecognitionSet(
        [
            new SqlObjectName("dbo", "MetricInputFact"),
            new SqlObjectName("dbo", "MachineObservation"),
            new SqlObjectName("dbo", "MetricInputFact")
        ]);

        Assert.Equal(2, set.OwnedTables.Length);
        Assert.Equal(new SqlObjectName("dbo", "MachineObservation"), set.OwnedTables[0]);
        Assert.Equal(new SqlObjectName("dbo", "MetricInputFact"), set.OwnedTables[1]);
        Assert.True(set.ContainsRepositoryIdentity(new SqlObjectName("dbo", "MetricInputFact")));
        Assert.False(set.ContainsRepositoryIdentity(new SqlObjectName("dbo", "CustomerOrders")));
    }
}