using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost011SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post010)
    {
        ArgumentNullException.ThrowIfNull(post010);

        return new SqlSchemaDescriptor(
        [
            .. post010.Tables,
            Table(
                "MetricAggregationRevision",
                [
                    Column("MetricAggregationProcessorRowId", "bigint"),
                    UInt64("Position")
                ],
                PrimaryKey("PK_MetricAggregationRevision",
                    "MetricAggregationProcessorRowId", "Position"),
                foreignKeys:
                [
                    ForeignKey(
                        "FK_MetricAggregationRevision_Processor",
                        ["MetricAggregationProcessorRowId"],
                        "MetricAggregationProcessor",
                        ["MetricAggregationProcessorRowId"])
                ],
                checks:
                [
                    Check(
                        "CK_MetricAggregationRevision_Position_UInt64Positive",
                        "([Position]>=(1) AND [Position]<=(18446744073709551615.))")
                ]),
            Table(
                "MetricAggregationRevisionShiftOccurrence",
                [
                    Column("MetricAggregationProcessorRowId", "bigint"),
                    UInt64("Position"),
                    Column("ShiftOccurrenceIdentityHash", "binary", 32),
                    ColumnMax("ShiftOccurrenceIdentityBinary", "varbinary"),
                    Column("SiteId", "nvarchar", 256, collation: BinaryCollation),
                    Column("SiteOrderKey", "varbinary", 769),
                    Column("ShiftScheduleAssignmentId", "nvarchar", 256, collation: BinaryCollation),
                    Column("ShiftScheduleAssignmentOrderKey", "varbinary", 769),
                    Column("ShiftId", "nvarchar", 256, collation: BinaryCollation),
                    Column("ShiftOrderKey", "varbinary", 769),
                    DateTimeOffset("ShiftStartsAtUtc", 7),
                    DateTimeOffset("ShiftEndsAtUtc", 7)
                ],
                PrimaryKey(
                    "PK_MetricAggregationRevisionShiftOccurrence",
                    "MetricAggregationProcessorRowId", "Position", "ShiftOccurrenceIdentityHash"),
                foreignKeys:
                [
                    ForeignKey(
                        "FK_MetricAggregationRevisionShiftOccurrence_Revision",
                        ["MetricAggregationProcessorRowId", "Position"],
                        "MetricAggregationRevision",
                        ["MetricAggregationProcessorRowId", "Position"])
                ],
                checks:
                [
                    Check(
                        "CK_MetricAggregationRevisionShiftOccurrence_Identity",
                        "(datalength([ShiftOccurrenceIdentityBinary])>(0) AND datalength([SiteId])>(0) AND datalength([SiteOrderKey])>=(1) AND datalength([SiteOrderKey])<=(769) AND datalength([ShiftScheduleAssignmentId])>(0) AND datalength([ShiftScheduleAssignmentOrderKey])>=(1) AND datalength([ShiftScheduleAssignmentOrderKey])<=(769) AND datalength([ShiftId])>(0) AND datalength([ShiftOrderKey])>=(1) AND datalength([ShiftOrderKey])<=(769))"),
                    Check(
                        "CK_MetricAggregationRevisionShiftOccurrence_Time",
                        "(datepart(tzoffset,[ShiftStartsAtUtc])=(0) AND datepart(tzoffset,[ShiftEndsAtUtc])=(0) AND [ShiftEndsAtUtc]>[ShiftStartsAtUtc])")
                ]),
            Table(
                "MetricAggregationRevisionProductionDay",
                [
                    Column("MetricAggregationProcessorRowId", "bigint"),
                    UInt64("Position"),
                    Column("SiteId", "nvarchar", 256, collation: BinaryCollation),
                    Column("SiteOrderKey", "varbinary", 769),
                    Column("ProductionBusinessDate", "date")
                ],
                PrimaryKey(
                    "PK_MetricAggregationRevisionProductionDay",
                    "MetricAggregationProcessorRowId", "Position", "SiteOrderKey", "ProductionBusinessDate"),
                foreignKeys:
                [
                    ForeignKey(
                        "FK_MetricAggregationRevisionProductionDay_Revision",
                        ["MetricAggregationProcessorRowId", "Position"],
                        "MetricAggregationRevision",
                        ["MetricAggregationProcessorRowId", "Position"])
                ],
                checks:
                [
                    Check(
                        "CK_MetricAggregationRevisionProductionDay_Site",
                        "(datalength([SiteId])>(0) AND datalength([SiteOrderKey])>=(1) AND datalength([SiteOrderKey])<=(769))")
                ])
        ]);
    }

    private static SqlTableDescriptor Table(
        string name,
        ImmutableArray<SqlColumnDescriptor> columns,
        SqlPrimaryKeyDescriptor primaryKey,
        ImmutableArray<SqlForeignKeyDescriptor> foreignKeys = default,
        ImmutableArray<SqlCheckConstraintDescriptor> checks = default) =>
        new(
            new SqlObjectName("dbo", name),
            columns,
            primaryKey,
            [],
            foreignKeys.IsDefault ? [] : foreignKeys,
            checks.IsDefault ? [] : checks,
            []);

    private static SqlColumnDescriptor Column(
        string name,
        string sqlType,
        int? maxLength = null,
        string? collation = null) =>
        new(
            name,
            sqlType,
            maxLength is null ? null : SqlLengthDescriptor.Bounded(maxLength.Value),
            Precision: null,
            Scale: null,
            IsNullable: false,
            collation,
            Identity: null);

    private static SqlColumnDescriptor ColumnMax(string name, string sqlType) =>
        new(
            name,
            sqlType,
            SqlLengthDescriptor.Max,
            Precision: null,
            Scale: null,
            IsNullable: false,
            Collation: null,
            Identity: null);

    private static SqlColumnDescriptor UInt64(string name) =>
        new(name, "decimal", null, 20, 0, false, null, null);

    private static SqlColumnDescriptor DateTimeOffset(string name, byte scale) =>
        new(name, "datetimeoffset", null, null, scale, false, null, null);

    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) =>
        new(name, IndexStructure(columns));

    private static SqlIndexStructureDescriptor IndexStructure(params string[] columns) =>
        new(
            IsClustered: true,
            columns.Select(static (column, index) =>
                new SqlIndexColumnDescriptor(
                    column,
                    SqlIndexColumnDirection.Ascending,
                    index + 1)).ToImmutableArray(),
            IncludedColumns: [],
            CanonicalFilterDefinition: null);

    private static SqlForeignKeyDescriptor ForeignKey(
        string name,
        string[] columns,
        string referencedTable,
        string[] referencedColumns) =>
        new(
            name,
            columns.ToImmutableArray(),
            new SqlObjectName("dbo", referencedTable),
            referencedColumns.ToImmutableArray(),
            SqlReferentialAction.NoAction,
            SqlReferentialAction.NoAction,
            IsEnabled: true,
            IsTrusted: true,
            IsNotForReplication: false);

    private static SqlCheckConstraintDescriptor Check(string name, string definition) =>
        new(name, definition, true, true, false);
}
