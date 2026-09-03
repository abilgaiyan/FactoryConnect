using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositorySchemaDescriptors
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor LegacyPost004 { get; } = CreatePost004();

    public static SqlSchemaDescriptor Current { get; } = CreatePost004();

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
            Column("MachineId", 1, "uniqueidentifier"),
            Column("StreamKeyBinary", 2, "varbinary", 512),
            Column("StreamKey", 3, "nvarchar", 256, collation: BinaryCollation),
            Decimal("InstanceId", 4, 20, 0),
            Decimal("NextSequence", 5, 20, 0)
        ],
        primaryKey: PrimaryKey("PK_ObservationStreamCheckpoint", "MachineId", "StreamKeyBinary"),
        checks:
        [
            Check("CK_ObservationStreamCheckpoint_InstanceId_UInt64", "InstanceId >= 0 AND InstanceId <= 18446744073709551615"),
            Check("CK_ObservationStreamCheckpoint_NextSequence_UInt64", "NextSequence >= 0 AND NextSequence <= 18446744073709551615")
        ]);

    private static SqlTableDescriptor MachineObservation() => Table(
        "MachineObservation",
        columns:
        [
            Column("MachineId", 1, "uniqueidentifier"),
            Column("StreamKeyBinary", 2, "varbinary", 512),
            Decimal("InstanceId", 3, 20, 0),
            Decimal("Sequence", 4, 20, 0),
            Column("Source", 5, "nvarchar", 256, collation: BinaryCollation),
            Column("Address", 6, "nvarchar", 512, collation: BinaryCollation),
            Column("SignalType", 7, "tinyint"),
            ColumnMax("ObservationValue", 8, "nvarchar", isNullable: true, collation: BinaryCollation),
            Column("Quality", 9, "tinyint"),
            DateTimeOffset("ObservedAt", 10, 7)
        ],
        primaryKey: PrimaryKey("PK_MachineObservation", "MachineId", "StreamKeyBinary", "InstanceId", "Sequence"),
        foreignKeys:
        [
            ForeignKey("FK_MachineObservation_ObservationStreamCheckpoint", ["MachineId", "StreamKeyBinary"], "ObservationStreamCheckpoint", ["MachineId", "StreamKeyBinary"])
        ],
        checks:
        [
            Check("CK_MachineObservation_InstanceId_UInt64", "InstanceId >= 0 AND InstanceId <= 18446744073709551615"),
            Check("CK_MachineObservation_Sequence_UInt64", "Sequence >= 0 AND Sequence <= 18446744073709551615"),
            Check("CK_MachineObservation_SignalType", "SignalType BETWEEN 0 AND 7"),
            Check("CK_MachineObservation_Quality", "Quality BETWEEN 0 AND 2")
        ]);

    private static SqlTableDescriptor MetricInputStream() => Table(
        "MetricInputStream",
        columns:
        [
            IdentityBigInt("MetricInputStreamRowId", 1),
            Column("MachineId", 2, "uniqueidentifier"),
            Column("StreamKeyBinary", 3, "varbinary", 512),
            Column("StreamKey", 4, "nvarchar", 256, collation: BinaryCollation)
        ],
        primaryKey: PrimaryKey("PK_MetricInputStream", "MetricInputStreamRowId"),
        uniques:
        [
            Unique("UQ_MetricInputStream_Identity", "MachineId", "StreamKeyBinary"),
            Unique("UQ_MetricInputStream_RowMachine", "MetricInputStreamRowId", "MachineId")
        ]);

    private static SqlTableDescriptor MetricInputFact() => Table(
        "MetricInputFact",
        columns:
        [
            IdentityBigInt("MetricInputFactRowId", 1),
            Column("MetricInputStreamRowId", 2, "bigint"),
            Decimal("Position", 3, 20, 0),
            Column("FactIdBinary", 4, "varbinary", 512),
            Column("FactId", 5, "nvarchar", 256, collation: BinaryCollation),
            Column("MetricInputKey", 6, "nvarchar", 256, collation: BinaryCollation),
            Column("MetricValue", 7, "nvarchar", 64, collation: BinaryCollation),
            Column("Unit", 8, "nvarchar", 128, collation: BinaryCollation),
            DateTimeOffset("StartsAtUtc", 9, 7),
            DateTimeOffset("EndsAtUtc", 10, 7),
            Column("CompanyId", 11, "nvarchar", 256, collation: BinaryCollation),
            Column("SiteId", 12, "nvarchar", 256, collation: BinaryCollation),
            Column("ProductionLineId", 13, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("MachineId", 14, "uniqueidentifier"),
            Column("ShiftId", 15, "nvarchar", 256, collation: BinaryCollation),
            Column("ShiftScheduleAssignmentId", 16, "nvarchar", 256, collation: BinaryCollation),
            Column("ProductionContextAssignmentId", 17, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("ProductionOrderId", 18, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("OperationId", 19, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("PartId", 20, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("OperatorId", 21, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("IsPlannedProductionTime", 22, "bit", isNullable: true),
            Column("PlannedProductionScheduleAssignmentId", 23, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("SourceContextualizedActivityIntervalId", 24, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("SourceEligibilityIntervalId", 25, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("SourceQuantityEvidenceId", 26, "nvarchar", 256, isNullable: true, collation: BinaryCollation),
            Column("OccurrenceSiteId", 27, "nvarchar", 256, collation: BinaryCollation),
            Column("OccurrenceShiftScheduleAssignmentId", 28, "nvarchar", 256, collation: BinaryCollation),
            Column("OccurrenceShiftId", 29, "nvarchar", 256, collation: BinaryCollation),
            DateTimeOffset("OccurrenceStartsAtUtc", 30, 7),
            DateTimeOffset("OccurrenceEndsAtUtc", 31, 7),
            Column("ProductionDaySiteId", 32, "nvarchar", 256, collation: BinaryCollation),
            Column("ProductionBusinessDate", 33, "date")
        ],
        primaryKey: PrimaryKey("PK_MetricInputFact", "MetricInputFactRowId"),
        uniques:
        [
            Unique("UQ_MetricInputFact_StreamPosition", "MetricInputStreamRowId", "Position"),
            Unique("UQ_MetricInputFact_StreamFactIdentity", "MetricInputStreamRowId", "FactIdBinary"),
            Unique("UQ_MetricInputFact_StreamPositionRow", "MetricInputFactRowId", "MetricInputStreamRowId", "Position")
        ],
        foreignKeys:
        [
            ForeignKey("FK_MetricInputFact_StreamMachine", ["MetricInputStreamRowId", "MachineId"], "MetricInputStream", ["MetricInputStreamRowId", "MachineId"])
        ],
        checks:
        [
            Check("CK_MetricInputFact_Position_UInt64", "Position >= 1 AND Position <= 18446744073709551615"),
            Check("CK_MetricInputFact_Interval", "EndsAtUtc >= StartsAtUtc"),
            Check("CK_MetricInputFact_OccurrenceInterval", "OccurrenceEndsAtUtc > OccurrenceStartsAtUtc"),
            Check("CK_MetricInputFact_OwnershipContainment", "StartsAtUtc >= OccurrenceStartsAtUtc AND EndsAtUtc <= OccurrenceEndsAtUtc"),
            Check("CK_MetricInputFact_SiteOwnership", "SiteId = OccurrenceSiteId AND SiteId = ProductionDaySiteId"),
            Check("CK_MetricInputFact_ShiftOwnership", "ShiftId = OccurrenceShiftId"),
            Check("CK_MetricInputFact_ScheduleOwnership", "ShiftScheduleAssignmentId = OccurrenceShiftScheduleAssignmentId"),
            Check("CK_MetricInputFact_UtcOffsets", "DATEPART(TZOFFSET, StartsAtUtc) = 0 AND DATEPART(TZOFFSET, EndsAtUtc) = 0 AND DATEPART(TZOFFSET, OccurrenceStartsAtUtc) = 0 AND DATEPART(TZOFFSET, OccurrenceEndsAtUtc) = 0")
        ],
        indexes:
        [
            Index("IX_MetricInputFact_OrderedRead", ["MetricInputStreamRowId", "Position"], ["MetricInputFactRowId", "FactId", "MetricInputKey", "MetricValue", "Unit"])
        ]);

    private static SqlTableDescriptor MetricAggregationProcessor() => Table(
        "MetricAggregationProcessor",
        columns:
        [
            IdentityBigInt("MetricAggregationProcessorRowId", 1),
            Column("ProcessorKeyBinary", 2, "varbinary", 512),
            Column("ProcessorKey", 3, "nvarchar", 256, collation: BinaryCollation),
            Column("MetricInputStreamRowId", 4, "bigint")
        ],
        primaryKey: PrimaryKey("PK_MetricAggregationProcessor", "MetricAggregationProcessorRowId"),
        uniques:
        [
            Unique("UQ_MetricAggregationProcessor_Identity", "ProcessorKeyBinary"),
            Unique("UQ_MetricAggregationProcessor_StreamBinding", "MetricAggregationProcessorRowId", "MetricInputStreamRowId")
        ],
        foreignKeys:
        [
            ForeignKey("FK_MetricAggregationProcessor_MetricInputStream", ["MetricInputStreamRowId"], "MetricInputStream", ["MetricInputStreamRowId"])
        ]);

    private static SqlTableDescriptor MetricAggregationCheckpoint() => Table(
        "MetricAggregationCheckpoint",
        columns:
        [
            Column("MetricAggregationProcessorRowId", 1, "bigint"),
            Decimal("Position", 2, 20, 0)
        ],
        primaryKey: PrimaryKey("PK_MetricAggregationCheckpoint", "MetricAggregationProcessorRowId"),
        foreignKeys:
        [
            ForeignKey("FK_MetricAggregationCheckpoint_Processor", ["MetricAggregationProcessorRowId"], "MetricAggregationProcessor", ["MetricAggregationProcessorRowId"])
        ],
        checks:
        [
            Check("CK_MetricAggregationCheckpoint_Position_UInt64", "Position >= 1 AND Position <= 18446744073709551615")
        ]);

    private static SqlTableDescriptor MetricAggregationContribution() => Table(
        "MetricAggregationContribution",
        columns:
        [
            Column("MetricAggregationProcessorRowId", 1, "bigint"),
            Column("MetricInputStreamRowId", 2, "bigint"),
            Column("MetricInputFactRowId", 3, "bigint"),
            Decimal("Position", 4, 20, 0)
        ],
        primaryKey: PrimaryKey("PK_MetricAggregationContribution", "MetricAggregationProcessorRowId", "MetricInputFactRowId"),
        uniques:
        [
            Unique("UQ_MetricAggregationContribution_Position", "MetricAggregationProcessorRowId", "Position")
        ],
        foreignKeys:
        [
            ForeignKey("FK_MetricAggregationContribution_ProcessorStream", ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"], "MetricAggregationProcessor", ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"]),
            ForeignKey("FK_MetricAggregationContribution_FactStreamPosition", ["MetricInputFactRowId", "MetricInputStreamRowId", "Position"], "MetricInputFact", ["MetricInputFactRowId", "MetricInputStreamRowId", "Position"])
        ],
        checks:
        [
            Check("CK_MetricAggregationContribution_Position_UInt64", "Position >= 1 AND Position <= 18446744073709551615")
        ]);

    private static SqlTableDescriptor ShiftMetricAggregate() => Table(
        "ShiftMetricAggregate",
        columns:
        [
            IdentityBigInt("ShiftMetricAggregateRowId", 1),
            Column("MetricAggregationProcessorRowId", 2, "bigint"),
            Column("AggregateKeyHash", 3, "binary", 32),
            ColumnMax("AggregateKeyBinary", 4, "varbinary"),
            Column("MachineId", 5, "uniqueidentifier"),
            Column("SiteId", 6, "nvarchar", 256, collation: BinaryCollation),
            Column("ShiftScheduleAssignmentId", 7, "nvarchar", 256, collation: BinaryCollation),
            Column("ShiftId", 8, "nvarchar", 256, collation: BinaryCollation),
            DateTimeOffset("ShiftStartsAtUtc", 9, 7),
            DateTimeOffset("ShiftEndsAtUtc", 10, 7),
            Column("MetricInputKey", 11, "nvarchar", 256, collation: BinaryCollation),
            Column("AggregateValue", 12, "nvarchar", 64, collation: BinaryCollation),
            Column("Unit", 13, "nvarchar", 128, collation: BinaryCollation),
            Column("InputCount", 14, "bigint"),
            DateTimeOffset("FirstInputTimestamp", 15, 7),
            DateTimeOffset("LastInputTimestamp", 16, 7)
        ],
        primaryKey: PrimaryKey("PK_ShiftMetricAggregate", "ShiftMetricAggregateRowId"),
        uniques:
        [
            Unique("UQ_ShiftMetricAggregate_IdentityHash", "MetricAggregationProcessorRowId", "AggregateKeyHash")
        ],
        foreignKeys:
        [
            ForeignKey("FK_ShiftMetricAggregate_Processor", ["MetricAggregationProcessorRowId"], "MetricAggregationProcessor", ["MetricAggregationProcessorRowId"])
        ],
        checks:
        [
            Check("CK_ShiftMetricAggregate_ShiftInterval", "ShiftEndsAtUtc > ShiftStartsAtUtc"),
            Check("CK_ShiftMetricAggregate_InputCount", "InputCount > 0"),
            Check("CK_ShiftMetricAggregate_InputInterval", "LastInputTimestamp >= FirstInputTimestamp"),
            Check("CK_ShiftMetricAggregate_UtcOffsets", "DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0 AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0 AND DATEPART(TZOFFSET, FirstInputTimestamp) = 0 AND DATEPART(TZOFFSET, LastInputTimestamp) = 0")
        ],
        indexes:
        [
            Index("IX_ShiftMetricAggregate_Query", ["MachineId", "ShiftStartsAtUtc", "ShiftEndsAtUtc"], ["MetricInputKey", "AggregateValue", "Unit", "InputCount"])
        ]);

    private static SqlTableDescriptor ProductionDayMetricAggregate() => Table(
        "ProductionDayMetricAggregate",
        columns:
        [
            IdentityBigInt("ProductionDayMetricAggregateRowId", 1),
            Column("MetricAggregationProcessorRowId", 2, "bigint"),
            Column("AggregateKeyHash", 3, "binary", 32),
            ColumnMax("AggregateKeyBinary", 4, "varbinary"),
            Column("MachineId", 5, "uniqueidentifier"),
            Column("SiteId", 6, "nvarchar", 256, collation: BinaryCollation),
            Column("ProductionBusinessDate", 7, "date"),
            Column("MetricInputKey", 8, "nvarchar", 256, collation: BinaryCollation),
            Column("AggregateValue", 9, "nvarchar", 64, collation: BinaryCollation),
            Column("Unit", 10, "nvarchar", 128, collation: BinaryCollation),
            Column("InputCount", 11, "bigint"),
            DateTimeOffset("FirstInputTimestamp", 12, 7),
            DateTimeOffset("LastInputTimestamp", 13, 7)
        ],
        primaryKey: PrimaryKey("PK_ProductionDayMetricAggregate", "ProductionDayMetricAggregateRowId"),
        uniques:
        [
            Unique("UQ_ProductionDayMetricAggregate_IdentityHash", "MetricAggregationProcessorRowId", "AggregateKeyHash")
        ],
        foreignKeys:
        [
            ForeignKey("FK_ProductionDayMetricAggregate_Processor", ["MetricAggregationProcessorRowId"], "MetricAggregationProcessor", ["MetricAggregationProcessorRowId"])
        ],
        checks:
        [
            Check("CK_ProductionDayMetricAggregate_InputCount", "InputCount > 0"),
            Check("CK_ProductionDayMetricAggregate_InputInterval", "LastInputTimestamp >= FirstInputTimestamp"),
            Check("CK_ProductionDayMetricAggregate_UtcOffsets", "DATEPART(TZOFFSET, FirstInputTimestamp) = 0 AND DATEPART(TZOFFSET, LastInputTimestamp) = 0")
        ],
        indexes:
        [
            Index("IX_ProductionDayMetricAggregate_Query", ["MachineId", "ProductionBusinessDate"], ["MetricInputKey", "AggregateValue", "Unit", "InputCount"])
        ]);

    private static SqlTableDescriptor ProductionContextProcessor() => Table(
        "ProductionContextProcessor",
        columns:
        [
            IdentityBigInt("ProductionContextProcessorRowId", 1),
            Column("ProcessorKeyHash", 2, "binary", 32),
            Column("ProcessorKeyBinary", 3, "varbinary", 512),
            Column("ProcessorKey", 4, "nvarchar", 256, collation: BinaryCollation),
            Column("MachineId", 5, "uniqueidentifier"),
            Column("ObservationStreamKeyHash", 6, "binary", 32),
            Column("ObservationStreamKeyBinary", 7, "varbinary", 512),
            Column("ObservationStreamKey", 8, "nvarchar", 256, collation: BinaryCollation)
        ],
        primaryKey: PrimaryKey("PK_ProductionContextProcessor", "ProductionContextProcessorRowId"),
        uniques:
        [
            Unique("UQ_ProductionContextProcessor_Identity", "ProcessorKeyHash", "MachineId", "ObservationStreamKeyHash")
        ]);

    private static SqlTableDescriptor ProductionContextCheckpoint() => Table(
        "ProductionContextCheckpoint",
        columns:
        [
            Column("ProductionContextProcessorRowId", 1, "bigint"),
            Decimal("Position", 2, 20, 0)
        ],
        primaryKey: PrimaryKey("PK_ProductionContextCheckpoint", "ProductionContextProcessorRowId"),
        foreignKeys:
        [
            ForeignKey("FK_ProductionContextCheckpoint_Processor", ["ProductionContextProcessorRowId"], "ProductionContextProcessor", ["ProductionContextProcessorRowId"])
        ],
        checks:
        [
            Check("CK_ProductionContextCheckpoint_Position_UInt64", "Position >= 1 AND Position <= 18446744073709551615")
        ]);

    private static SqlTableDescriptor ContextualizedActivityOutput() => OutputTable(
        "ContextualizedActivityOutput",
        "ContextualizedActivityOutputRowId",
        "PK_ContextualizedActivityOutput",
        "UQ_ContextualizedActivityOutput_IdentityHash");

    private static SqlTableDescriptor ProductionTimeEligibilityOutput() => OutputTable(
        "ProductionTimeEligibilityOutput",
        "ProductionTimeEligibilityOutputRowId",
        "PK_ProductionTimeEligibilityOutput",
        "UQ_ProductionTimeEligibilityOutput_IdentityHash");

    private static SqlTableDescriptor OutputTable(string tableName, string rowId, string primaryKeyName, string uniqueName) => Table(
        tableName,
        columns:
        [
            IdentityBigInt(rowId, 1),
            Column("IdentityHash", 2, "binary", 32),
            Column("IdentityBinary", 3, "varbinary", 512),
            Column("IdentityText", 4, "nvarchar", 256, collation: BinaryCollation),
            Column("PayloadHash", 5, "binary", 32),
            ColumnMax("Payload", 6, "nvarchar", collation: BinaryCollation)
        ],
        primaryKey: PrimaryKey(primaryKeyName, rowId),
        uniques: [Unique(uniqueName, "IdentityHash")]);

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

    private static SqlColumnDescriptor IdentityBigInt(string name, int ordinal) => new(
        name,
        ordinal,
        "bigint",
        MaxLength: null,
        Precision: null,
        Scale: null,
        IsNullable: false,
        Collation: null,
        Identity: new SqlIdentityDescriptor(1m, 1m, IsNotForReplication: false));

    private static SqlColumnDescriptor Decimal(string name, int ordinal, byte precision, byte scale) => new(
        name,
        ordinal,
        "decimal",
        MaxLength: null,
        Precision: precision,
        Scale: scale,
        IsNullable: false,
        Collation: null,
        Identity: null);

    private static SqlColumnDescriptor DateTimeOffset(string name, int ordinal, byte scale) => new(
        name,
        ordinal,
        "datetimeoffset",
        MaxLength: null,
        Precision: null,
        Scale: scale,
        IsNullable: false,
        Collation: null,
        Identity: null);

    private static SqlColumnDescriptor Column(
        string name,
        int ordinal,
        string sqlType,
        int? maxLength = null,
        bool isNullable = false,
        string? collation = null) => new(
            name,
            ordinal,
            sqlType,
            maxLength is null ? null : SqlLengthDescriptor.Bounded(maxLength.Value),
            Precision: null,
            Scale: null,
            isNullable,
            collation,
            Identity: null);

    private static SqlColumnDescriptor ColumnMax(
        string name,
        int ordinal,
        string sqlType,
        bool isNullable = false,
        string? collation = null) => new(
            name,
            ordinal,
            sqlType,
            SqlLengthDescriptor.Max,
            Precision: null,
            Scale: null,
            isNullable,
            collation,
            Identity: null);

    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) => new(
        name,
        IndexStructure(isClustered: true, columns));

    private static SqlUniqueConstraintDescriptor Unique(string name, params string[] columns) => new(
        name,
        IndexStructure(isClustered: false, columns));

    private static SqlIndexDescriptor Index(string name, string[] keys, string[] includes) => new(
        name,
        IsUnique: false,
        IsEnabled: true,
        new SqlIndexStructureDescriptor(
            IsClustered: false,
            KeyColumns: IndexColumns(keys),
            IncludedColumns: includes.ToImmutableArray(),
            CanonicalFilterDefinition: null));

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
