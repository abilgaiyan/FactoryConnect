using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost013SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post012)
    {
        ArgumentNullException.ThrowIfNull(post012);

        return new SqlSchemaDescriptor(
        [
            .. post012.Tables,
            Table(
                "ProductionStandardAuthorityRevision",
                [UInt64("ProductionStandardAuthorityRevision")],
                PrimaryKey("PK_ProductionStandardAuthorityRevision", "ProductionStandardAuthorityRevision"),
                checks:
                [Check("CK_ProductionStandardAuthorityRevision_UInt64", "([ProductionStandardAuthorityRevision]>=(0) AND [ProductionStandardAuthorityRevision]<=(18446744073709551615.))")]),
            Table(
                "ProductionStandardVersion",
                [
                    Text("VersionId"),
                    UInt64("ProductionStandardAuthorityRevision"),
                    Text("CompanyId"),
                    Text("SiteId"),
                    Text("PartId"),
                    Text("OperationId"),
                    Column("MachineId", "uniqueidentifier", isNullable: true),
                    Decimal("SecondsPerUnit", 20, 6),
                    DateTimeOffset("EffectiveFromUtc", 7),
                    DateTimeOffset("EffectiveToUtc", 7, isNullable: true),
                    Text("SourceReference", 1024)
                ],
                PrimaryKey("PK_ProductionStandardVersion", "VersionId"),
                uniques:
                [Unique("UQ_ProductionStandardVersion_Revision", "ProductionStandardAuthorityRevision")],
                foreignKeys:
                [ForeignKey("FK_ProductionStandardVersion_AuthorityRevision", ["ProductionStandardAuthorityRevision"], "ProductionStandardAuthorityRevision", ["ProductionStandardAuthorityRevision"])],
                checks:
                [
                    Check("CK_ProductionStandardVersion_RevisionPositive", "([ProductionStandardAuthorityRevision]>=(1) AND [ProductionStandardAuthorityRevision]<=(18446744073709551615.))"),
                    Check("CK_ProductionStandardVersion_SecondsPerUnit", "([SecondsPerUnit]>=(0))"),
                    Check("CK_ProductionStandardVersion_EffectiveInterval", "([EffectiveToUtc] IS NULL OR [EffectiveToUtc]>[EffectiveFromUtc])"),
                    Check("CK_ProductionStandardVersion_Utc", "(datepart(tzoffset,[EffectiveFromUtc])=(0) AND ([EffectiveToUtc] IS NULL OR datepart(tzoffset,[EffectiveToUtc])=(0)))")
                ])
        ]);
    }

    private static SqlTableDescriptor Table(string name, ImmutableArray<SqlColumnDescriptor> columns, SqlPrimaryKeyDescriptor primaryKey, ImmutableArray<SqlUniqueConstraintDescriptor> uniques = default, ImmutableArray<SqlForeignKeyDescriptor> foreignKeys = default, ImmutableArray<SqlCheckConstraintDescriptor> checks = default) =>
        new(new SqlObjectName("dbo", name), columns, primaryKey, uniques.IsDefault ? [] : uniques, foreignKeys.IsDefault ? [] : foreignKeys, checks.IsDefault ? [] : checks, []);

    private static SqlColumnDescriptor Text(string name, int maxLength = 256, bool isNullable = false) => Column(name, "nvarchar", maxLength, isNullable, BinaryCollation);
    private static SqlColumnDescriptor Column(string name, string sqlType, int? maxLength = null, bool isNullable = false, string? collation = null) => new(name, sqlType, maxLength is null ? null : SqlLengthDescriptor.Bounded(maxLength.Value), null, null, isNullable, collation, null);
    private static SqlColumnDescriptor UInt64(string name) => Decimal(name, 20, 0);
    private static SqlColumnDescriptor Decimal(string name, byte precision, byte scale, bool isNullable = false) => new(name, "decimal", null, precision, scale, isNullable, null, null);
    private static SqlColumnDescriptor DateTimeOffset(string name, byte scale, bool isNullable = false) => new(name, "datetimeoffset", null, null, scale, isNullable, null, null);
    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) => new(name, IndexStructure(columns));
    private static SqlUniqueConstraintDescriptor Unique(string name, params string[] columns) => new(name, true, new SqlIndexStructureDescriptor(false, columns.Select(static (column, index) => new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1)).ToImmutableArray(), [], null));
    private static SqlIndexStructureDescriptor IndexStructure(params string[] columns) => new(true, columns.Select(static (column, index) => new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1)).ToImmutableArray(), [], null);
    private static SqlForeignKeyDescriptor ForeignKey(string name, string[] columns, string referencedTable, string[] referencedColumns) => new(name, columns.ToImmutableArray(), new SqlObjectName("dbo", referencedTable), referencedColumns.ToImmutableArray(), SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false);
    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(name, definition, true, true, false);
}
