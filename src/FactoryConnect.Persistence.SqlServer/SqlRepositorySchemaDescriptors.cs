using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositorySchemaDescriptors
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor LegacyPost004 { get; } = CreatePost004();

    public static SqlSchemaDescriptor Post005 { get; } =
        SqlRepositoryPost005SchemaDescriptor.Create(LegacyPost004);

    public static SqlSchemaDescriptor Current { get; } =
        SqlRepositoryPost006SchemaDescriptor.Create(Post005);

    private static SqlSchemaDescriptor CreatePost004() => new(
    [
        ObservationStreamCheckpoint(),
        MachineObservation(),
        MetricInputStream(),
        MetricInputFact(),
        MetricAggregationProcessor(),
        MetricAggregationCheckpoint(),
        MetricAggregationContribution(),
        ShiftMetricAggregate(),
        ProductionDayMetricAggregate(),
        ProductionContextProcessor(),
        ProductionContextCheckpoint(),
        ContextualizedActivityOutput(),
        ProductionTimeEligibilityOutput()
    ]);

    private static SqlTableDescriptor ObservationStreamCheckpoint() => Table(
        "ObservationStreamCheckpoint",
        columns:
        [
            Column("MachineId", "uniqueidentifier"),
            Column("StreamKeyBinary", "varbinary", 512),
            Column("StreamKey", "nvarchar", 256, collation: BinaryCollation),
            Decimal("InstanceId", 20, 0),
            Decimal("NextSequence", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_ObservationStreamCheckpoint", "MachineId", "StreamKeyBinary"),
        checks:
        [
            Check("CK_ObservationStreamCheckpoint_InstanceId_UInt64", "([InstanceId]>=(0) AND [InstanceId]<=(18446744073709551615.))"),
            Check("CK_ObservationStreamCheckpoint_NextSequence_UInt64", "([NextSequence]>=(0) AND [NextSequence]<=(18446744073709551615.))")
        ]),

    private static SqlTableDescriptor MachineObservation() => Table(
        "MachineObservation",
        columns:
        [
            IdentityBigInt("MachineObservationRowId"),
            Column("MachineId", "uniqueidentifier"),
            Column("StreamKeyBinary", "varbinary", 512),
            Column("StreamKey", "nvarchar", 256, collation: BinaryCollation),
            Decimal("InstanceId", 20, 0),
            Decimal("Sequence", 20, 0),
            Column("ObservedAtUtc", "datetimeoffset", precision: 7),
            Column("ObservedAtUnixMilliseconds", "bigint"),
            Column("DataItemId", "nvarchar", 256, collation: BinaryCollation),
            Column("DataItemType", "nvarchar", 256, collation: BinaryCollation),
            Column("ValueType", "tinyint"),
            Column("ValueText", "nvarchar", 2048, isNullable: true, collation: BinaryCollation),
            Column("ValueDecimal", "decimal", precision: 38, scale: 18, isNullable: true),
            Column("ValueInteger", "bigint", isNullable: true),
            Column("ValueBoolean", "bit", isNullable: true)
        ],
        primaryKey: PrimaryKey("PK_MachineObservation", "MachineObservationRowId"),
        uniques:
        [
            Unique("UQ_MachineObservation_StreamSequence", "MachineId", "StreamKeyBinary", "InstanceId", "Sequence")
        ],
        checks:
        [
            Check("CK_MachineObservation_InstanceId_UInt64", "([InstanceId]>=(0) AND [InstanceId]<=(18446744073709551615.))"),
            Check("CK_MachineObservation_Sequence_UInt64", "([Sequence]>=(0) AND [Sequence]<=(18446744073709551615.))"),
            Check("CK_MachineObservation_ValueType", "([ValueType]>=(0) AND [ValueType]<=(3))")
        ]),

    private static SqlTableDescriptor MetricInputStream() => Table(
        "MetricInputStream",
        columns:
        [
            IdentityBigInt("MetricInputStreamRowId"),
            Column("MachineId", "uniqueidentifier"),
            Column("StreamKeyBinary", "varbinary", 512),
            Column("StreamKey", "nvarchar", 256, collation: BinaryCollation)
        ],
        primaryKey: PrimaryKey("PK_MetricInputStream", "MetricInputStreamRowId"),
        uniques:
        [
            Unique("UQ_MetricInputStream_MachineStream", "MachineId", "StreamKeyBinary")
        ]),

    private static SqlTableDescriptor MetricInputFact() => Table(
        "MetricInputFact",
        columns:
        [
            IdentityBigInt("MetricInputFactRowId"),
            Column("MetricInputStreamRowId", "bigint"),
            Column("MachineId", "uniqueidentifier"),
            Column("MetricKey", "nvarchar", 256, collation: BinaryCollation),
            Column("ObservedAtUtc", "datetimeoffset", precision: 7),
            Decimal("RevisionPosition", 20, 0),
            Column("Value", "decimal", precision: 38, scale: 18),
            Column("Unit", "nvarchar", 128, collation: BinaryCollation)
        ],
        primaryKey: PrimaryKey("PK_MetricInputFact", "MetricInputFactRowId"),
        foreignKeys:
        [
            ForeignKey(
                "FK_MetricInputFact_MetricInputStream",
                ["MetricInputStreamRowId"],
                "MetricInputStream",
                ["MetricInputStreamRowId"])
        ]),

    private static SqlTableDescriptor MetricAggregationProcessor() => Table(
        "MetricAggregationProcessor",
        columns:
        [
            IdentityBigInt("MetricAggregationProcessorRowId"),
            Column("ProcessorKey", "nvarchar", 256, collation: BinaryCollation),
            Column("MetricInputStreamRowId", "bigint")
        ],
        primaryKey: PrimaryKey("PK_MetricAggregationProcessor", "MetricAggregationProcessorRowId"),
        uniques:
        [
            Unique("UQ_MetricAggregationProcessor_ProcessorKey", "ProcessorKey"),
            Unique("UQ_MetricAggregationProcessor_StreamBinding", "MetricAggregationProcessorRowId", "MetricInputStreamRowId")
        ],
        foreignKeys:
        [
            ForeignKey(
                "FK_MetricAggregationProcessor_MetricInputStream",
                ["MetricInputStreamRowId"],
                "MetricInputStream",
                ["MetricInputStreamRowId"])
        ]),

    private static SqlTableDescriptor MetricAggregationCheckpoint() => Table(
        "MetricAggregationCheckpoint",
        columns:
        [
            Column("MetricAggregationProcessorRowId", "bigint"),
            Decimal("Position", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_MetricAggregationCheckpoint", "MetricAggregationProcessorRowId"),
        foreignKeys:
        [
            ForeignKey(
                "FK_MetricAggregationCheckpoint_Processor",
                ["MetricAggregationProcessorRowId"],
                "MetricAggregationProcessor",
                ["MetricAggregationProcessorRowId"])
        ]),

    private static SqlTableDescriptor MetricAggregationContribution() => Table(
        "MetricAggregationContribution",
        columns:
        [
            IdentityBigInt("MetricAggregationContributionRowId"),
            Column("MetricAggregationProcessorRowId", "bigint"),
            Decimal("RevisionPosition", 20, 0),
            Column("MetricKey", "nvarchar", 256, collation: BinaryCollation),
            Column("Value", "decimal", precision: 38, scale: 18),
            Column("Unit", "nvarchar", 128, collation: BinaryCollation)
        ],
        primaryKey: PrimaryKey("PK_MetricAggregationContribution", "MetricAggregationContributionRowId"),
        foreignKeys:
        [
            ForeignKey(
                "FK_MetricAggregationContribution_Processor",
                ["MetricAggregationProcessorRowId"],
                "MetricAggregationProcessor",
                ["MetricAggregationProcessorRowId"])
        ]),

    private static SqlTableDescriptor ShiftMetricAggregate() => Table(
        "ShiftMetricAggregate",
        columns:
        [
            IdentityBigInt("ShiftMetricAggregateRowId"),
            Column("MetricAggregationProcessorRowId", "bigint"),
            Column("MachineId", "uniqueidentifier"),
            Column("ShiftId", "nvarchar", 256, collation: BinaryCollation),
            Column("ShiftStartsAtUtc", "datetimeoffset", precision: 7),
            Column("ShiftEndsAtUtc", "datetimeoffset", precision: 7),
            Column("MetricKey", "nvarchar", 256, collation: BinaryCollation),
            Column("MetricValue", "decimal", precision: 38, scale: 18),
            Column("Unit", "nvarchar", 128, collation: BinaryCollation),
            Decimal("SourceRevisionPosition", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_ShiftMetricAggregate", "ShiftMetricAggregateRowId")),

    private static SqlTableDescriptor ProductionDayMetricAggregate() => Table(
        "ProductionDayMetricAggregate",
        columns:
        [
            IdentityBigInt("ProductionDayMetricAggregateRowId"),
            Column("MetricAggregationProcessorRowId", "bigint"),
            Column("MachineId", "uniqueidentifier"),
            Column("ProductionBusinessDate", "date"),
            Column("MetricKey", "nvarchar", 256, collation: BinaryCollation),
            Column("MetricValue", "decimal", precision: 38, scale: 18),
            Column("Unit", "nvarchar", 128, collation: BinaryCollation),
            Decimal("SourceRevisionPosition", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_ProductionDayMetricAggregate", "ProductionDayMetricAggregateRowId")),

    private static SqlTableDescriptor ProductionContextProcessor() => Table(
        "ProductionContextProcessor",
        columns:
        [
            IdentityBigInt("ProductionContextProcessorRowId"),
            Column("ProcessorKey", "nvarchar", 256, collation: BinaryCollation),
            Column("MetricInputStreamRowId", "bigint")
        ],
        primaryKey: PrimaryKey("PK_ProductionContextProcessor", "ProductionContextProcessorRowId"),
        uniques:
        [
            Unique("UQ_ProductionContextProcessor_ProcessorKey", "ProcessorKey")
        ]),

    private static SqlTableDescriptor ProductionContextCheckpoint() => Table(
        "ProductionContextCheckpoint",
        columns:
        [
            Column("ProductionContextProcessorRowId", "bigint"),
            Decimal("Position", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_ProductionContextCheckpoint", "ProductionContextProcessorRowId")),

    private static SqlTableDescriptor ContextualizedActivityOutput() => Table(
        "ContextualizedActivityOutput",
        columns:
        [
            IdentityBigInt("ContextualizedActivityOutputRowId"),
            Column("ProductionContextProcessorRowId", "bigint"),
            Column("MachineId", "uniqueidentifier"),
            Column("StartsAtUtc", "datetimeoffset", precision: 7),
            Column("EndsAtUtc", "datetimeoffset", precision: 7)
        ],
        primaryKey: PrimaryKey("PK_ContextualizedActivityOutput", "ContextualizedActivityOutputRowId")),

    private static SqlTableDescriptor ProductionTimeEligibilityOutput() => Table(
        "ProductionTimeEligibilityOutput",
        columns:
        [
            IdentityBigInt("ProductionTimeEligibilityOutputRowId"),
            Column("ProductionContextProcessorRowId", "bigint"),
            Column("MachineId", "uniqueidentifier"),
            Column("StartsAtUtc", "datetimeoffset", precision: 7),
            Column("EndsAtUtc", "datetimeoffset", precision: 7)
        ],
        primaryKey: PrimaryKey("PK_ProductionTimeEligibilityOutput", "ProductionTimeEligibilityOutputRowId")),

    private static SqlTableDescriptor Table(
        string tableName,
        SqlColumnDescriptor[] columns,
        SqlPrimaryKeyDescriptor? primaryKey = null,
        SqlUniqueConstraintDescriptor[]? uniques = null,
        SqlForeignKeyDescriptor[]? foreignKeys = null,
        SqlCheckConstraintDescriptor[]? checks = null) => new(
            new SqlObjectName("dbo", tableName),
            [.. columns],
            primaryKey,
            [.. uniques ?? []],
            [.. foreignKeys ?? []],
            [.. checks ?? []],
            []);

    private static SqlColumnDescriptor IdentityBigInt(string name) => new(
        name,
        "bigint",
        null,
        null,
        null,
        false,
        null,
        new SqlIdentityDescriptor(1m, 1m, false));

    private static SqlColumnDescriptor Column(
        string name,
        string sqlType,
        int? maxLength = null,
        byte? precision = null,
        byte? scale = null,
        bool isNullable = false,
        string? collation = null) => new(
            name,
            sqlType,
            maxLength is null ? null : SqlLengthDescriptor.Bounded(maxLength.Value),
            precision,
            scale,
            isNullable,
            collation,
            null);

    private static SqlColumnDescriptor Decimal(
        string name,
        byte precision,
        byte scale,
        bool isNullable = false) =>
        Column(name, "decimal", precision: precision, scale: scale, isNullable: isNullable);

    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) =>
        new(name, IndexStructure(isClustered: true, columns));

    private static SqlUniqueConstraintDescriptor Unique(string name, params string[] columns) =>
        new(name, IndexStructure(isClustered: false, columns));

    private static SqlForeignKeyDescriptor ForeignKey(
        string name,
        string[] columns,
        string referencedTable,
        string[] referencedColumns) => new(
            name,
            [.. columns],
            new SqlObjectName("dbo", referencedTable),
            [.. referencedColumns],
            SqlReferentialAction.NoAction,
            SqlReferentialAction.NoAction,
            true,
            true,
            false);

    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(
        name,
        definition,
        true,
        true,
        false);

    private static SqlIndexStructureDescriptor IndexStructure(
        bool isClustered,
        params string[] columns) => new(
            isClustered,
            [.. columns.Select(static (column, index) => new SqlIndexColumnDescriptor(
                column,
                SqlIndexColumnDirection.Ascending,
                index + 1))],
            [],
            null);
}
