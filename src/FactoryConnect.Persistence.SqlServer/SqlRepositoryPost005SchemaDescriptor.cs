using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost005SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor legacyPost004) => new(
    [
        .. legacyPost004.Tables,
        OperationalMetricProjectionProcessor(),
        OperationalMetricProjectionCheckpoint(),
        OperationalMetricProjection(),
        OperationalMetricProjectionManifest(),
        OperationalMetricProjectionEvidence(),
        MachineShiftOccurrenceRoster(),
        MachineShiftOccurrenceRosterOccurrence()
    ]);

    private static SqlTableDescriptor OperationalMetricProjectionProcessor() => Table(
        "OperationalMetricProjectionProcessor",
        columns:
        [
            IdentityBigInt("OperationalMetricProjectionProcessorRowId"),
            Column("ProcessorKeyBinary", "varbinary", 769),
            Column("ProcessorKey", "nvarchar", 256, collation: BinaryCollation),
            Column("MetricAggregationProcessorRowId", "bigint"),
            Column("MetricInputStreamRowId", "bigint")
        ],
        primaryKey: PrimaryKey("PK_OperationalMetricProjectionProcessor", true, "OperationalMetricProjectionProcessorRowId"),
        uniques:
        [
            Unique("UQ_OperationalMetricProjectionProcessor_ProcessorKeyBinary", "ProcessorKeyBinary")
        ],
        foreignKeys:
        [
            ForeignKey(
                "FK_OperationalMetricProjectionProcessor_MetricAggregationProcessor",
                ["MetricAggregationProcessorRowId"],
                "MetricAggregationProcessor",
                ["MetricAggregationProcessorRowId"]),
            ForeignKey(
                "FK_OperationalMetricProjectionProcessor_MetricAggregationProcessorStream",
                ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"],
                "MetricAggregationProcessor",
                ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"])
        ],
        checks:
        [
            Check("CK_OperationalMetricProjectionProcessor_ProcessorKey", "DATALENGTH(ProcessorKey) > 0"),
            Check("CK_OperationalMetricProjectionProcessor_ProcessorKeyBinary", "DATALENGTH(ProcessorKeyBinary) BETWEEN 1 AND 769")
        ]);

    private static SqlTableDescriptor OperationalMetricProjectionCheckpoint() => Table(
        "OperationalMetricProjectionCheckpoint",
        columns:
        [
            Column("OperationalMetricProjectionProcessorRowId", "bigint"),
            Decimal("Position", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_OperationalMetricProjectionCheckpoint", true, "OperationalMetricProjectionProcessorRowId"),
        foreignKeys:
        [
            ForeignKey(
                "FK_OperationalMetricProjectionCheckpoint_ProjectionProcessor",
                ["OperationalMetricProjectionProcessorRowId"],
                "OperationalMetricProjectionProcessor",
                ["OperationalMetricProjectionProcessorRowId"])
        ],
        checks:
        [
            Check("CK_OperationalMetricProjectionCheckpoint_Position", "Position BETWEEN 1 AND 18446744073709551615")
        ]);

    private static SqlTableDescriptor OperationalMetricProjection() => Table(
        "OperationalMetricProjection",
        columns:
        [
            IdentityBigInt("OperationalMetricProjectionRowId"),
            Column("OperationalMetricProjectionProcessorRowId", "bigint"),
            Column("EvaluationKeyCodecVersion", "smallint"),
            Column("EvaluationKeyHash", "binary", 32),
            ColumnMax("EvaluationKeyBinary", "varbinary"),
            Column("MachineId", "uniqueidentifier"),
            ComputedBinary(
                "MachineOrderKey",
                16,
                "CONVERT(binary(16), REPLACE(CONVERT(char(36), MachineId), '-', ''), 2)",
                isPersisted: true,
                isNullable: false),
            Column("PeriodKind", "tinyint"),
            Column("PeriodSiteId", "nvarchar", 256, collation: BinaryCollation),
            Column("PeriodSiteOrderKey", "varbinary", 769),
            Column("ShiftScheduleAssignmentId", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("ShiftScheduleAssignmentOrderKey", "varbinary", 769, isNullable: true),
            Column("ShiftId", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("ShiftOrderKey", "varbinary", 769, isNullable: true),
            DateTimeOffset("ShiftStartsAtUtc", 7, isNullable: true),
            DateTimeOffset("ShiftEndsAtUtc", 7, isNullable: true),
            Column("ProductionBusinessDate", "date", isNullable: true),
            Column("ProductionOrderPresent", "bit"),
            Column("ProductionOrderId", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("ProductionOrderOrderKey", "varbinary", 769, isNullable: true),
            Column("OperationPresent", "bit"),
            Column("OperationId", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("OperationOrderKey", "varbinary", 769, isNullable: true),
            Column("PartPresent", "bit"),
            Column("PartId", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("PartOrderKey", "varbinary", 769, isNullable: true),
            Column("OperatorPresent", "bit"),
            Column("OperatorId", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("OperatorOrderKey", "varbinary", 769, isNullable: true),
            Column("MetricKey", "nvarchar", 256, collation: BinaryCollation),
            Column("MetricKeyOrderKey", "varbinary", 769),
            Column("DefinitionVersion", "nvarchar", 256, collation: BinaryCollation),
            Column("DefinitionVersionOrderKey", "varbinary", 769),
            Column("Status", "tinyint"),
            Column("MetricValue", "nvarchar", 64, isNullable: true, collation: BinaryCollation),
            Column("Unit", "nvarchar", 128, collation: BinaryCollation),
            Column("ReasonCode", "tinyint", isNullable: true),
            Column("ReasonOperandName", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Decimal("SourceRevisionPosition", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_OperationalMetricProjection", true, "OperationalMetricProjectionRowId"),
        uniques:
        [
            Unique("UQ_OperationalMetricProjection_LogicalHash", "OperationalMetricProjectionProcessorRowId", "EvaluationKeyHash"),
            Unique("UQ_OperationalMetricProjection_ProcessorRow", "OperationalMetricProjectionProcessorRowId", "OperationalMetricProjectionRowId")
        ],
        foreignKeys:
        [
            ForeignKey(
                "FK_OperationalMetricProjection_ProjectionProcessor",
                ["OperationalMetricProjectionProcessorRowId"],
                "OperationalMetricProjectionProcessor",
                ["OperationalMetricProjectionProcessorRowId"])
        ],
        checks:
        [
            Check("CK_OperationalMetricProjection_EvaluationKeyCodecVersion", "EvaluationKeyCodecVersion = 1"),
            Check("CK_OperationalMetricProjection_EvaluationKeyBinaryLength", "DATALENGTH(EvaluationKeyBinary) BETWEEN 1 AND 6992"),
            Check("CK_OperationalMetricProjection_PeriodShape", PeriodShape),
            Check("CK_OperationalMetricProjection_ProductionOrderContext", ContextShape("ProductionOrder")),
            Check("CK_OperationalMetricProjection_OperationContext", ContextShape("Operation")),
            Check("CK_OperationalMetricProjection_PartContext", ContextShape("Part")),
            Check("CK_OperationalMetricProjection_OperatorContext", ContextShape("Operator")),
            Check("CK_OperationalMetricProjection_MetricIdentity", MetricIdentityShape),
            Check("CK_OperationalMetricProjection_SourceRevisionPosition", "SourceRevisionPosition BETWEEN 1 AND 18446744073709551615"),
            Check("CK_OperationalMetricProjection_Status", "Status IN (0,1,2)"),
            Check("CK_OperationalMetricProjection_ReasonCode", "ReasonCode IS NULL OR ReasonCode IN (0,1,2,3,4,5)"),
            Check("CK_OperationalMetricProjection_StatusShape", StatusShape)
        ],
        indexes:
        [
            FilteredIndex(
                "IX_OperationalMetricProjection_ShiftWindow",
                ["OperationalMetricProjectionProcessorRowId", "ShiftStartsAtUtc", "MachineOrderKey", "OperationalMetricProjectionRowId"],
                "PeriodKind = 1"),
            FilteredIndex(
                "IX_OperationalMetricProjection_ProductionDayWindow",
                ["OperationalMetricProjectionProcessorRowId", "ProductionBusinessDate", "MachineOrderKey", "OperationalMetricProjectionRowId"],
                "PeriodKind = 2")
        ]);

    private static SqlTableDescriptor OperationalMetricProjectionManifest() => Table(
        "OperationalMetricProjectionManifest",
        columns:
        [
            Column("OperationalMetricProjectionProcessorRowId", "bigint"),
            Column("OperationalMetricProjectionRowId", "bigint")
        ],
        primaryKey: PrimaryKey(
            "PK_OperationalMetricProjectionManifest",
            true,
            "OperationalMetricProjectionProcessorRowId",
            "OperationalMetricProjectionRowId"),
        foreignKeys:
        [
            ForeignKey(
                "FK_OperationalMetricProjectionManifest_Checkpoint",
                ["OperationalMetricProjectionProcessorRowId"],
                "OperationalMetricProjectionCheckpoint",
                ["OperationalMetricProjectionProcessorRowId"]),
            ForeignKey(
                "FK_OperationalMetricProjectionManifest_Projection",
                ["OperationalMetricProjectionProcessorRowId", "OperationalMetricProjectionRowId"],
                "OperationalMetricProjection",
                ["OperationalMetricProjectionProcessorRowId", "OperationalMetricProjectionRowId"])
        ]);

    private static SqlTableDescriptor OperationalMetricProjectionEvidence() => Table(
        "OperationalMetricProjectionEvidence",
        columns:
        [
            IdentityBigInt("OperationalMetricProjectionEvidenceRowId"),
            Column("OperationalMetricProjectionRowId", "bigint"),
            Column("EvidenceKind", "tinyint"),
            Column("EvidenceOrdinal", "int"),
            Column("OperandName", "nvarchar", 256, collation: BinaryCollation),
            Column("OperandNameOrderKey", "varbinary", 769),
            Column("ComponentKey", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("MetricDimension", "tinyint", isNullable: true),
            Column("ComponentValue", "nvarchar", 64, isNullable: true, collation: BinaryCollation),
            Column("ComponentUnit", "nvarchar", 128, isNullable: true, collation: BinaryCollation),
            Decimal("InputCount", 20, 0, isNullable: true),
            DateTimeOffset("FirstInputTimestamp", 7, isNullable: true),
            DateTimeOffset("LastInputTimestamp", 7, isNullable: true),
            Column("DependencyMetricKey", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("DependencyDefinitionVersion", "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("DependencySnapshotCodecVersion", "smallint", isNullable: true),
            Column("DependencySnapshotHash", "binary", 32, isNullable: true),
            ColumnMax("DependencySnapshotBinary", "varbinary", isNullable: true)
        ],
        primaryKey: PrimaryKey("PK_OperationalMetricProjectionEvidence", true, "OperationalMetricProjectionEvidenceRowId"),
        uniques:
        [
            Unique("UQ_OperationalMetricProjectionEvidence_Operand", "OperationalMetricProjectionRowId", "OperandNameOrderKey"),
            Unique("UQ_OperationalMetricProjectionEvidence_Ordinal", "OperationalMetricProjectionRowId", "EvidenceKind", "EvidenceOrdinal")
        ],
        foreignKeys:
        [
            ForeignKey(
                "FK_OperationalMetricProjectionEvidence_Projection",
                ["OperationalMetricProjectionRowId"],
                "OperationalMetricProjection",
                ["OperationalMetricProjectionRowId"])
        ],
        checks:
        [
            Check("CK_OperationalMetricProjectionEvidence_Operand", "DATALENGTH(OperandName) > 0 AND DATALENGTH(OperandNameOrderKey) BETWEEN 1 AND 769"),
            Check("CK_OperationalMetricProjectionEvidence_Ordinal", "EvidenceOrdinal >= 0"),
            Check("CK_OperationalMetricProjectionEvidence_Kind", "EvidenceKind IN (1,2)"),
            Check("CK_OperationalMetricProjectionEvidence_SubtypeShape", EvidenceSubtypeShape)
        ]);

    private static SqlTableDescriptor MachineShiftOccurrenceRoster() => Table(
        "MachineShiftOccurrenceRoster",
        columns:
        [
            IdentityBigInt("MachineShiftOccurrenceRosterRowId"),
            Column("MachineId", "uniqueidentifier"),
            Column("ProductionDaySiteId", "nvarchar", 256, collation: BinaryCollation),
            Column("ProductionDaySiteOrderKey", "varbinary", 769),
            Column("ProductionBusinessDate", "date"),
            Column("ProductionLineId", "nvarchar", 256, collation: BinaryCollation),
            Decimal("Revision", 20, 0)
        ],
        primaryKey: PrimaryKey("PK_MachineShiftOccurrenceRoster", true, "MachineShiftOccurrenceRosterRowId"),
        uniques:
        [
            Unique("UQ_MachineShiftOccurrenceRoster_Identity", "MachineId", "ProductionDaySiteOrderKey", "ProductionBusinessDate")
        ],
        checks:
        [
            Check("CK_MachineShiftOccurrenceRoster_Site", "DATALENGTH(ProductionDaySiteId) > 0 AND DATALENGTH(ProductionDaySiteOrderKey) BETWEEN 1 AND 769"),
            Check("CK_MachineShiftOccurrenceRoster_ProductionLine", "DATALENGTH(ProductionLineId) > 0"),
            Check("CK_MachineShiftOccurrenceRoster_Revision", "Revision BETWEEN 1 AND 18446744073709551615")
        ]);

    private static SqlTableDescriptor MachineShiftOccurrenceRosterOccurrence() => Table(
        "MachineShiftOccurrenceRosterOccurrence",
        columns:
        [
            Column("MachineShiftOccurrenceRosterRowId", "bigint"),
            Column("ShiftScheduleAssignmentId", "nvarchar", 256, collation: BinaryCollation),
            Column("ShiftScheduleAssignmentOrderKey", "varbinary", 769),
            Column("ShiftId", "nvarchar", 256, collation: BinaryCollation),
            Column("ShiftOrderKey", "varbinary", 769),
            DateTimeOffset("ShiftStartsAtUtc", 7),
            DateTimeOffset("ShiftEndsAtUtc", 7)
        ],
        primaryKey: PrimaryKey(
            "PK_MachineShiftOccurrenceRosterOccurrence",
            false,
            "MachineShiftOccurrenceRosterRowId",
            "ShiftScheduleAssignmentOrderKey",
            "ShiftOrderKey",
            "ShiftStartsAtUtc",
            "ShiftEndsAtUtc"),
        foreignKeys:
        [
            ForeignKey(
                "FK_MachineShiftOccurrenceRosterOccurrence_Roster",
                ["MachineShiftOccurrenceRosterRowId"],
                "MachineShiftOccurrenceRoster",
                ["MachineShiftOccurrenceRosterRowId"])
        ],
        checks:
        [
            Check("CK_MachineShiftOccurrenceRosterOccurrence_ShiftIdentity", ShiftIdentityShape),
            Check("CK_MachineShiftOccurrenceRosterOccurrence_Time", OccurrenceTimeShape)
        ]);

    private const string PeriodShape = """
        (
            PeriodKind = 1
            AND PeriodSiteId IS NOT NULL
            AND DATALENGTH(PeriodSiteId) > 0
            AND PeriodSiteOrderKey IS NOT NULL
            AND DATALENGTH(PeriodSiteOrderKey) BETWEEN 1 AND 769
            AND ShiftScheduleAssignmentId IS NOT NULL
            AND DATALENGTH(ShiftScheduleAssignmentId) > 0
            AND ShiftScheduleAssignmentOrderKey IS NOT NULL
            AND DATALENGTH(ShiftScheduleAssignmentOrderKey) BETWEEN 1 AND 769
            AND ShiftId IS NOT NULL
            AND DATALENGTH(ShiftId) > 0
            AND ShiftOrderKey IS NOT NULL
            AND DATALENGTH(ShiftOrderKey) BETWEEN 1 AND 769
            AND ShiftStartsAtUtc IS NOT NULL
            AND ShiftEndsAtUtc IS NOT NULL
            AND DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0
            AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0
            AND ShiftEndsAtUtc > ShiftStartsAtUtc
            AND ProductionBusinessDate IS NULL
        )
        OR
        (
            PeriodKind = 2
            AND PeriodSiteId IS NOT NULL
            AND DATALENGTH(PeriodSiteId) > 0
            AND PeriodSiteOrderKey IS NOT NULL
            AND DATALENGTH(PeriodSiteOrderKey) BETWEEN 1 AND 769
            AND ProductionBusinessDate IS NOT NULL
            AND ShiftScheduleAssignmentId IS NULL
            AND ShiftScheduleAssignmentOrderKey IS NULL
            AND ShiftId IS NULL
            AND ShiftOrderKey IS NULL
            AND ShiftStartsAtUtc IS NULL
            AND ShiftEndsAtUtc IS NULL
        )
        """;

    private static string ContextShape(string prefix) => $"""
        (
            {prefix}Present = 0
            AND {prefix}Id IS NULL
            AND {prefix}OrderKey IS NULL
        )
        OR
        (
            {prefix}Present = 1
            AND {prefix}Id IS NOT NULL
            AND DATALENGTH({prefix}Id) > 0
            AND {prefix}OrderKey IS NOT NULL
            AND DATALENGTH({prefix}OrderKey) BETWEEN 1 AND 769
        )
        """;

    private const string MetricIdentityShape = """
        DATALENGTH(MetricKey) > 0
        AND DATALENGTH(MetricKeyOrderKey) BETWEEN 1 AND 769
        AND DATALENGTH(DefinitionVersion) > 0
        AND DATALENGTH(DefinitionVersionOrderKey) BETWEEN 1 AND 769
        AND DATALENGTH(Unit) > 0
        """;

    private const string StatusShape = """
        (
            Status = 0
            AND MetricValue IS NOT NULL
            AND DATALENGTH(MetricValue) > 0
            AND ReasonCode IS NULL
            AND ReasonOperandName IS NULL
        )
        OR
        (
            Status IN (1,2)
            AND MetricValue IS NULL
            AND ReasonCode IS NOT NULL
            AND ReasonCode IN (0,1,2,3,4,5)
            AND (ReasonOperandName IS NULL OR DATALENGTH(ReasonOperandName) > 0)
        )
        """;

    private const string EvidenceSubtypeShape = """
        (
            EvidenceKind = 1
            AND ComponentKey IS NOT NULL
            AND DATALENGTH(ComponentKey) > 0
            AND MetricDimension IS NOT NULL
            AND MetricDimension IN (0,1,2)
            AND ComponentValue IS NOT NULL
            AND DATALENGTH(ComponentValue) > 0
            AND ComponentUnit IS NOT NULL
            AND DATALENGTH(ComponentUnit) > 0
            AND InputCount IS NOT NULL
            AND InputCount BETWEEN 1 AND 9223372036854775807
            AND FirstInputTimestamp IS NOT NULL
            AND LastInputTimestamp IS NOT NULL
            AND DATEPART(TZOFFSET, FirstInputTimestamp) = 0
            AND DATEPART(TZOFFSET, LastInputTimestamp) = 0
            AND LastInputTimestamp >= FirstInputTimestamp
            AND DependencyMetricKey IS NULL
            AND DependencyDefinitionVersion IS NULL
            AND DependencySnapshotCodecVersion IS NULL
            AND DependencySnapshotHash IS NULL
            AND DependencySnapshotBinary IS NULL
        )
        OR
        (
            EvidenceKind = 2
            AND ComponentKey IS NULL
            AND MetricDimension IS NULL
            AND ComponentValue IS NULL
            AND ComponentUnit IS NULL
            AND InputCount IS NULL
            AND FirstInputTimestamp IS NULL
            AND LastInputTimestamp IS NULL
            AND DependencyMetricKey IS NOT NULL
            AND DATALENGTH(DependencyMetricKey) > 0
            AND DependencyDefinitionVersion IS NOT NULL
            AND DATALENGTH(DependencyDefinitionVersion) > 0
            AND DependencySnapshotCodecVersion IS NOT NULL
            AND DependencySnapshotCodecVersion = 1
            AND DependencySnapshotHash IS NOT NULL
            AND DependencySnapshotBinary IS NOT NULL
            AND DATALENGTH(DependencySnapshotBinary) BETWEEN 1 AND 16777216
        )
        """;

    private const string ShiftIdentityShape = """
        DATALENGTH(ShiftScheduleAssignmentId) > 0
        AND DATALENGTH(ShiftScheduleAssignmentOrderKey) BETWEEN 1 AND 769
        AND DATALENGTH(ShiftId) > 0
        AND DATALENGTH(ShiftOrderKey) BETWEEN 1 AND 769
        """;

    private const string OccurrenceTimeShape = """
        DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0
        AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0
        AND ShiftEndsAtUtc > ShiftStartsAtUtc
        """;

    private static SqlTableDescriptor Table(
        string name,
        ImmutableArray<SqlColumnDescriptor> columns,
        SqlPrimaryKeyDescriptor? primaryKey = null,
        ImmutableArray<SqlUniqueConstraintDescriptor> uniques = default,
        ImmutableArray<SqlForeignKeyDescriptor> foreignKeys = default,
        ImmutableArray<SqlCheckConstraintDescriptor> checks = default,
        ImmutableArray<SqlIndexDescriptor> indexes = default) => new(
            new SqlObjectName("dbo", name),
            columns,
            primaryKey,
            EmptyIfDefault(uniques),
            EmptyIfDefault(foreignKeys),
            EmptyIfDefault(checks),
            EmptyIfDefault(indexes));

    private static SqlColumnDescriptor IdentityBigInt(string name) => new(
        name,
        "bigint",
        MaxLength: null,
        Precision: null,
        Scale: null,
        IsNullable: false,
        Collation: null,
        Identity: new SqlIdentityDescriptor(1m, 1m, IsNotForReplication: false));

    private static SqlColumnDescriptor Decimal(string name, byte precision, byte scale, bool isNullable = false) => new(
        name,
        "decimal",
        MaxLength: null,
        Precision: precision,
        Scale: scale,
        IsNullable: isNullable,
        Collation: null,
        Identity: null);

    private static SqlColumnDescriptor DateTimeOffset(string name, byte scale, bool isNullable = false) => new(
        name,
        "datetimeoffset",
        MaxLength: null,
        Precision: null,
        Scale: scale,
        IsNullable: isNullable,
        Collation: null,
        Identity: null);

    private static SqlColumnDescriptor Column(
        string name,
        string sqlType,
        int? maxLength = null,
        bool isNullable = false,
        string? collation = null) => new(
            name,
            sqlType,
            maxLength is null ? null : SqlLengthDescriptor.Bounded(maxLength.Value),
            Precision: null,
            Scale: null,
            IsNullable: isNullable,
            Collation: collation,
            Identity: null);

    private static SqlColumnDescriptor ColumnMax(
        string name,
        string sqlType,
        bool isNullable = false,
        string? collation = null) => new(
            name,
            sqlType,
            SqlLengthDescriptor.Max,
            Precision: null,
            Scale: null,
            IsNullable: isNullable,
            Collation: collation,
            Identity: null);

    private static SqlColumnDescriptor ComputedBinary(
        string name,
        int length,
        string definition,
        bool isPersisted,
        bool isNullable) => new(
            name,
            "binary",
            SqlLengthDescriptor.Bounded(length),
            Precision: null,
            Scale: null,
            IsNullable: isNullable,
            Collation: null,
            Identity: null,
            Computed: new SqlComputedDescriptor(definition, isPersisted));

    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, bool isClustered, params string[] columns) => new(
        name,
        IsEnabled: true,
        IndexStructure(isClustered, columns));

    private static SqlUniqueConstraintDescriptor Unique(string name, params string[] columns) => new(
        name,
        IsEnabled: true,
        IndexStructure(isClustered: false, columns));

    private static SqlIndexDescriptor FilteredIndex(string name, string[] keys, string filter) => new(
        name,
        IsUnique: false,
        IsEnabled: true,
        new SqlIndexStructureDescriptor(
            IsClustered: false,
            KeyColumns: IndexColumns(keys),
            IncludedColumns: [],
            CanonicalFilterDefinition: filter));

    private static SqlIndexStructureDescriptor IndexStructure(bool isClustered, params string[] columns) => new(
        isClustered,
        IndexColumns(columns),
        IncludedColumns: [],
        CanonicalFilterDefinition: null);

    private static ImmutableArray<SqlIndexColumnDescriptor> IndexColumns(IEnumerable<string> columns) => columns
        .Select(static (column, index) => new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1))
        .ToImmutableArray();

    private static SqlForeignKeyDescriptor ForeignKey(
        string name,
        string[] columns,
        string referencedTable,
        string[] referencedColumns) => new(
            name,
            columns.ToImmutableArray(),
            new SqlObjectName("dbo", referencedTable),
            referencedColumns.ToImmutableArray(),
            SqlReferentialAction.NoAction,
            SqlReferentialAction.NoAction,
            IsEnabled: true,
            IsTrusted: true,
            IsNotForReplication: false);

    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(
        name,
        definition,
        IsEnabled: true,
        IsTrusted: true,
        IsNotForReplication: false);

    private static ImmutableArray<T> EmptyIfDefault<T>(ImmutableArray<T> values) =>
        values.IsDefault ? [] : values;
}
