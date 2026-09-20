using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost010SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post009)
    {
        ArgumentNullException.ThrowIfNull(post009);

        return new SqlSchemaDescriptor(
        [
            .. post009.Tables,
            new SqlTableDescriptor(
                new SqlObjectName("dbo", "ObservationProcessingCheckpoint"),
                [
                    Column("MachineId", "uniqueidentifier"),
                    Column("StreamKeyBinary", "varbinary", 512),
                    Column("ProcessorId", "nvarchar", 256, collation: BinaryCollation),
                    Column("ProcessorIdOrderKey", "varbinary", 769),
                    UInt64("Position")
                ],
                new SqlPrimaryKeyDescriptor(
                    "PK_ObservationProcessingCheckpoint",
                    IndexStructure(
                        "MachineId",
                        "StreamKeyBinary",
                        "ProcessorIdOrderKey")),
                [],
                [
                    ForeignKey(
                        "FK_ObservationProcessingCheckpoint_Stream",
                        ["MachineId", "StreamKeyBinary"],
                        "ObservationStreamCheckpoint",
                        ["MachineId", "StreamKeyBinary"]),
                    ForeignKey(
                        "FK_ObservationProcessingCheckpoint_Position",
                        ["MachineId", "StreamKeyBinary", "Position"],
                        "MachineObservation",
                        ["MachineId", "StreamKeyBinary", "Position"])
                ],
                [
                    Check(
                        "CK_ObservationProcessingCheckpoint_ProcessorId",
                        "(datalength([ProcessorId])>(0))"),
                    Check(
                        "CK_ObservationProcessingCheckpoint_ProcessorOrderKey",
                        "(datalength([ProcessorIdOrderKey])>=(1) AND datalength([ProcessorIdOrderKey])<=(769))"),
                    Check(
                        "CK_ObservationProcessingCheckpoint_Position_UInt64Positive",
                        "([Position]>=(1) AND [Position]<=(18446744073709551615.))")
                ],
                [])
        ]);
    }

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

    private static SqlColumnDescriptor UInt64(string name) =>
        new(
            name,
            "decimal",
            MaxLength: null,
            Precision: 20,
            Scale: 0,
            IsNullable: false,
            Collation: null,
            Identity: null);

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

    private static SqlCheckConstraintDescriptor Check(
        string name,
        string definition) =>
        new(
            name,
            definition,
            IsEnabled: true,
            IsTrusted: true,
            IsNotForReplication: false);
}
