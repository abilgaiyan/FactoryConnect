using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost007SchemaDescriptor
{
    private static readonly SqlObjectName MachineObservationName =
        new("dbo", "MachineObservation");

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post006)
    {
        ArgumentNullException.ThrowIfNull(post006);

        var machineObservation = post006.Tables.Single(
            static table => table.Name == MachineObservationName);

        var updatedMachineObservation = machineObservation with
        {
            Columns = [
                .. machineObservation.Columns,
                Decimal("Position", 20, 0)
            ],
            UniqueConstraints = [
                .. machineObservation.UniqueConstraints,
                Unique(
                    "UQ_MachineObservation_StreamPosition",
                    "MachineId",
                    "StreamKeyBinary",
                    "Position")
            ],
            CheckConstraints = [
                .. machineObservation.CheckConstraints,
                Check(
                    "CK_MachineObservation_Position_UInt64Positive",
                    "([Position]>=(1) AND [Position]<=(18446744073709551615.))")
            ]
        };

        var tables = post006.Tables
            .Select(table => table.Name == MachineObservationName
                ? updatedMachineObservation
                : table)
            .Append(AcquisitionContactAuthority())
            .ToImmutableArray();

        return new SqlSchemaDescriptor(tables);
    }

    private static SqlTableDescriptor AcquisitionContactAuthority() => new(
        new SqlObjectName("dbo", "AcquisitionContactAuthority"),
        [
            Column("MachineId", "uniqueidentifier"),
            Column("StreamKeyBinary", "varbinary", 512),
            DateTimeOffset("SuccessfulContactTime", 7),
            Decimal("RawAcceptedThrough", 20, 0, isNullable: true),
            Decimal("AcquisitionRevision", 20, 0)
        ],
        PrimaryKey(
            "PK_AcquisitionContactAuthority",
            "MachineId",
            "StreamKeyBinary"),
        [],
        [
            ForeignKey(
                "FK_AcquisitionContactAuthority_Checkpoint",
                ["MachineId", "StreamKeyBinary"],
                "ObservationStreamCheckpoint",
                ["MachineId", "StreamKeyBinary"]),
            ForeignKey(
                "FK_AcquisitionContactAuthority_RawAcceptedThrough",
                ["MachineId", "StreamKeyBinary", "RawAcceptedThrough"],
                "MachineObservation",
                ["MachineId", "StreamKeyBinary", "Position"])
        ],
        [
            Check(
                "CK_AcquisitionContactAuthority_SuccessfulContactTimeUtc",
                "(datepart(tzoffset,[SuccessfulContactTime])=(0))"),
            Check(
                "CK_AcquisitionContactAuthority_RawAcceptedThrough_UInt64Positive",
                "([RawAcceptedThrough] IS NULL OR [RawAcceptedThrough]>=(1) AND [RawAcceptedThrough]<=(18446744073709551615.))"),
            Check(
                "CK_AcquisitionContactAuthority_AcquisitionRevision_UInt64",
                "([AcquisitionRevision]>=(0) AND [AcquisitionRevision]<=(18446744073709551615.))")
        ],
        []);

    private static SqlColumnDescriptor Column(
        string name,
        string sqlType,
        int? maxLength = null,
        bool isNullable = false) => new(
            name,
            sqlType,
            maxLength is null ? null : SqlLengthDescriptor.Bounded(maxLength.Value),
            Precision: null,
            Scale: null,
            isNullable,
            Collation: null,
            Identity: null);

    private static SqlColumnDescriptor Decimal(
        string name,
        byte precision,
        byte scale,
        bool isNullable = false) => new(
            name,
            "decimal",
            MaxLength: null,
            precision,
            scale,
            isNullable,
            Collation: null,
            Identity: null);

    private static SqlColumnDescriptor DateTimeOffset(
        string name,
        byte scale) => new(
            name,
            "datetimeoffset",
            MaxLength: null,
            Precision: null,
            scale,
            IsNullable: false,
            Collation: null,
            Identity: null);

    private static SqlPrimaryKeyDescriptor PrimaryKey(
        string name,
        params string[] columns) => new(
            name,
            IndexStructure(isClustered: true, columns));

    private static SqlUniqueConstraintDescriptor Unique(
        string name,
        params string[] columns) => new(
            name,
            IndexStructure(isClustered: false, columns));

    private static SqlIndexStructureDescriptor IndexStructure(
        bool isClustered,
        params string[] columns) => new(
            isClustered,
            IndexColumns(columns),
            IncludedColumns: [],
            CanonicalFilterDefinition: null);

    private static ImmutableArray<SqlIndexColumnDescriptor> IndexColumns(
        IEnumerable<string> columns) => columns
            .Select(static (column, index) =>
                new SqlIndexColumnDescriptor(
                    column,
                    SqlIndexColumnDirection.Ascending,
                    index + 1))
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

    private static SqlCheckConstraintDescriptor Check(
        string name,
        string definition) => new(
            name,
            definition,
            IsEnabled: true,
            IsTrusted: true,
            IsNotForReplication: false);
}
