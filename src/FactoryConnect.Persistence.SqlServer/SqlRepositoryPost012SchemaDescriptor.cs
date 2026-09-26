using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost012SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post011)
    {
        ArgumentNullException.ThrowIfNull(post011);

        return new SqlSchemaDescriptor(
        [
            .. post011.Tables,
            Table(
                "ProductionReferenceTimeRevision",
                [UInt64("ProductionReferenceTimeRevision")],
                PrimaryKey("PK_ProductionReferenceTimeRevision", "ProductionReferenceTimeRevision"),
                checks:
                [Check("CK_ProductionReferenceTimeRevision_UInt64", "([ProductionReferenceTimeRevision]>=(0) AND [ProductionReferenceTimeRevision]<=(18446744073709551615.))")]),
            Table(
                "ProductionReferenceTimeOutcome",
                [
                    Column("MetricAggregationProcessorRowId", "bigint"),
                    UInt64("ProductionReferenceTimeRevision"),
                    Text("SourceQuantityEvidenceId"),
                    Text("CompanyId"),
                    Text("SiteId"),
                    Column("MachineId", "uniqueidentifier"),
                    Text("ShiftOccurrenceSiteId"),
                    Text("ShiftScheduleAssignmentId"),
                    Text("ShiftId"),
                    DateTimeOffset("ShiftStartsAtUtc", 7),
                    DateTimeOffset("ShiftEndsAtUtc", 7),
                    Text("ProductionDaySiteId"),
                    Column("ProductionBusinessDate", "date"),
                    Text("OperationId", isNullable: true),
                    Text("PartId", isNullable: true),
                    DateTimeOffset("OccurredAtUtc", 7),
                    Column("ProducedQuantity", "int"),
                    UInt64("ProductionStandardAuthorityRevision"),
                    Column("ResolutionStatus", "tinyint"),
                    Text("SelectedStandardVersionId", isNullable: true),
                    Text("SelectedStandardSourceReference", maxLength: 1024, isNullable: true),
                    Decimal("IdealProductionDurationSeconds", 20, 6, isNullable: true)
                ],
                PrimaryKey("PK_ProductionReferenceTimeOutcome", "ProductionReferenceTimeRevision", "SourceQuantityEvidenceId"),
                uniques:
                [Unique("UQ_ProductionReferenceTimeOutcome_SourceReplay", "MetricAggregationProcessorRowId", "SourceQuantityEvidenceId")],
                foreignKeys:
                [
                    ForeignKey("FK_ProductionReferenceTimeOutcome_Revision", ["ProductionReferenceTimeRevision"], "ProductionReferenceTimeRevision", ["ProductionReferenceTimeRevision"]),
                    ForeignKey("FK_ProductionReferenceTimeOutcome_AggregationAuthority", ["MetricAggregationProcessorRowId"], "MetricAggregationProcessor", ["MetricAggregationProcessorRowId"])
                ],
                checks:
                [
                    Check("CK_ProductionReferenceTimeOutcome_Revision_UInt64", "([ProductionReferenceTimeRevision]>=(0) AND [ProductionReferenceTimeRevision]<=(18446744073709551615.))"),
                    Check("CK_ProductionReferenceTimeOutcome_StandardRevision_UInt64", "([ProductionStandardAuthorityRevision]>=(0) AND [ProductionStandardAuthorityRevision]<=(18446744073709551615.))"),
                    Check("CK_ProductionReferenceTimeOutcome_ProducedQuantity", "([ProducedQuantity]>=(0))"),
                    Check("CK_ProductionReferenceTimeOutcome_Status", "([ResolutionStatus]>=(0) AND [ResolutionStatus]<=(3))"),
                    Check("CK_ProductionReferenceTimeOutcome_PeriodOwnership", "([SiteId]=[ShiftOccurrenceSiteId] AND [SiteId]=[ProductionDaySiteId] AND [ShiftEndsAtUtc]>[ShiftStartsAtUtc] AND [OccurredAtUtc]>=[ShiftStartsAtUtc] AND [OccurredAtUtc]<[ShiftEndsAtUtc])"),
                    Check("CK_ProductionReferenceTimeOutcome_Utc", "(datepart(tzoffset,[OccurredAtUtc])=(0) AND datepart(tzoffset,[ShiftStartsAtUtc])=(0) AND datepart(tzoffset,[ShiftEndsAtUtc])=(0))"),
                    Check("CK_ProductionReferenceTimeOutcome_IdealDuration", "([IdealProductionDurationSeconds] IS NULL OR [IdealProductionDurationSeconds]>=(0))")
                ]),
            Table(
                "ProductionReferenceTimeOutcomeConflict",
                [UInt64("ProductionReferenceTimeRevision"), Text("SourceQuantityEvidenceId"), Text("ConflictingStandardVersionId")],
                PrimaryKey("PK_ProductionReferenceTimeOutcomeConflict", "ProductionReferenceTimeRevision", "SourceQuantityEvidenceId", "ConflictingStandardVersionId"),
                foreignKeys:
                [ForeignKey("FK_ProductionReferenceTimeOutcomeConflict_Outcome", ["ProductionReferenceTimeRevision", "SourceQuantityEvidenceId"], "ProductionReferenceTimeOutcome", ["ProductionReferenceTimeRevision", "SourceQuantityEvidenceId"])]),
            Table(
                "ProductionReferenceTimePublicationCut",
                [Column("MetricAggregationProcessorRowId", "bigint"), UInt64("MetricAggregationPosition"), UInt64("ProductionReferenceTimeRevision")],
                PrimaryKey("PK_ProductionReferenceTimePublicationCut", "MetricAggregationProcessorRowId", "MetricAggregationPosition", "ProductionReferenceTimeRevision"),
                foreignKeys:
                [
                    ForeignKey("FK_ProductionReferenceTimePublicationCut_MetricAggregationRevision", ["MetricAggregationProcessorRowId", "MetricAggregationPosition"], "MetricAggregationRevision", ["MetricAggregationProcessorRowId", "Position"]),
                    ForeignKey("FK_ProductionReferenceTimePublicationCut_ReferenceTimeRevision", ["ProductionReferenceTimeRevision"], "ProductionReferenceTimeRevision", ["ProductionReferenceTimeRevision"])
                ],
                checks:
                [
                    Check("CK_ProductionReferenceTimePublicationCut_MetricPosition_UInt64Positive", "([MetricAggregationPosition]>=(1) AND [MetricAggregationPosition]<=(18446744073709551615.))"),
                    Check("CK_ProductionReferenceTimePublicationCut_ReferenceRevision_UInt64", "([ProductionReferenceTimeRevision]>=(0) AND [ProductionReferenceTimeRevision]<=(18446744073709551615.))")
                ])
        ]);
    }

    private static SqlTableDescriptor Table(string name, ImmutableArray<SqlColumnDescriptor> columns, SqlPrimaryKeyDescriptor primaryKey, ImmutableArray<SqlUniqueConstraintDescriptor> uniques = default, ImmutableArray<SqlForeignKeyDescriptor> foreignKeys = default, ImmutableArray<SqlCheckConstraintDescriptor> checks = default) =>
        new(new SqlObjectName("dbo", name), columns, primaryKey, uniques.IsDefault ? [] : uniques, foreignKeys.IsDefault ? [] : foreignKeys, checks.IsDefault ? [] : checks, []);

    private static SqlColumnDescriptor Text(string name, int maxLength = 256, bool isNullable = false) => Column(name, "nvarchar", maxLength, isNullable, BinaryCollation);

    private static SqlColumnDescriptor Column(string name, string sqlType, int? maxLength = null, bool isNullable = false, string? collation = null) =>
        new(name, sqlType, maxLength is null ? null : SqlLengthDescriptor.Bounded(maxLength.Value), null, null, isNullable, collation, null);

    private static SqlColumnDescriptor UInt64(string name) => Decimal(name, 20, 0);
    private static SqlColumnDescriptor Decimal(string name, byte precision, byte scale, bool isNullable = false) => new(name, "decimal", null, precision, scale, isNullable, null, null);
    private static SqlColumnDescriptor DateTimeOffset(string name, byte scale, bool isNullable = false) => new(name, "datetimeoffset", null, null, scale, isNullable, null, null);
    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) => new(name, IndexStructure(columns));
    private static SqlUniqueConstraintDescriptor Unique(string name, params string[] columns) => new(name, true, new SqlIndexStructureDescriptor(false, columns.Select(static (column, index) => new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1)).ToImmutableArray(), [], null));
    private static SqlIndexStructureDescriptor IndexStructure(params string[] columns) => new(true, columns.Select(static (column, index) => new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1)).ToImmutableArray(), [], null);
    private static SqlForeignKeyDescriptor ForeignKey(string name, string[] columns, string referencedTable, string[] referencedColumns) => new(name, columns.ToImmutableArray(), new SqlObjectName("dbo", referencedTable), referencedColumns.ToImmutableArray(), SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false);
    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(name, definition, true, true, false);
}
