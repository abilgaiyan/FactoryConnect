using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost008SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post007)
    {
        ArgumentNullException.ThrowIfNull(post007);

        return new SqlSchemaDescriptor(
        [
            .. post007.Tables,
            MappingCoverageAuthority(),
            MachineStateActivityAuthority(),
            MachineStateActivitySignal(),
            MachineStateChangeHistory(),
            MachineActivityPeriodHistory()
        ]);
    }

    private static SqlTableDescriptor MappingCoverageAuthority() => Table(
        "MappingCoverageAuthority",
        [
            Column("MachineId", "uniqueidentifier"), Column("StreamKeyBinary", "varbinary", 512),
            Column("MappingProcessorId", "nvarchar", 256, collation: BinaryCollation), Column("MappingProcessorIdOrderKey", "varbinary", 769),
            Decimal("RawConsumedThrough", 20, 0), Decimal("MappedEvaluationInputHighWater", 20, 0, true), Decimal("MappingRevision", 20, 0)
        ],
        PrimaryKey("PK_MappingCoverageAuthority", "MachineId", "StreamKeyBinary", "MappingProcessorIdOrderKey"),
        foreignKeys: [ForeignKey("FK_MappingCoverageAuthority_Checkpoint", ["MachineId", "StreamKeyBinary"], "ObservationStreamCheckpoint", ["MachineId", "StreamKeyBinary"])],
        checks:
        [
            Check("CK_MappingCoverageAuthority_ProcessorId", "(datalength([MappingProcessorId])>(0))"),
            Check("CK_MappingCoverageAuthority_ProcessorOrderKey", "(datalength([MappingProcessorIdOrderKey])>=(1) AND datalength([MappingProcessorIdOrderKey])<=(769))"),
            Check("CK_MappingCoverageAuthority_RawConsumedThrough_UInt64Positive", "([RawConsumedThrough]>=(1) AND [RawConsumedThrough]<=(18446744073709551615.))"),
            Check("CK_MappingCoverageAuthority_MappedHighWater_UInt64Positive", "([MappedEvaluationInputHighWater] IS NULL OR [MappedEvaluationInputHighWater]>=(1) AND [MappedEvaluationInputHighWater]<=(18446744073709551615.))"),
            Check("CK_MappingCoverageAuthority_HighWater", "([MappedEvaluationInputHighWater] IS NULL OR [MappedEvaluationInputHighWater]<=[RawConsumedThrough])"),
            Check("CK_MappingCoverageAuthority_MappingRevision_UInt64", "([MappingRevision]>=(0) AND [MappingRevision]<=(18446744073709551615.))")
        ]);

    private static SqlTableDescriptor MachineStateActivityAuthority() => Table(
        "MachineStateActivityAuthority",
        [
            Column("MachineId", "uniqueidentifier"), Column("StreamKeyBinary", "varbinary", 512),
            Column("StateProcessorId", "nvarchar", 256, collation: BinaryCollation), Column("StateProcessorIdOrderKey", "varbinary", 769),
            Decimal("Position", 20, 0), Column("MachineState", "tinyint"), Column("ActiveState", "tinyint", isNullable: true),
            DateTimeOffset("ActiveStartedAt", 7, true), Decimal("LastConsumedInstanceId", 20, 0),
            Column("ContinuityPolicyIdentity", "nvarchar", 256, collation: BinaryCollation), Column("ContinuityPolicyVersion", "nvarchar", 256, collation: BinaryCollation),
            Decimal("ProjectionRevision", 20, 0)
        ],
        PrimaryKey("PK_MachineStateActivityAuthority", "MachineId", "StreamKeyBinary", "StateProcessorIdOrderKey"),
        foreignKeys: [ForeignKey("FK_MachineStateActivityAuthority_Checkpoint", ["MachineId", "StreamKeyBinary"], "ObservationStreamCheckpoint", ["MachineId", "StreamKeyBinary"])],
        checks:
        [
            Check("CK_MachineStateActivityAuthority_ProcessorId", "(datalength([StateProcessorId])>(0))"),
            Check("CK_MachineStateActivityAuthority_ProcessorOrderKey", "(datalength([StateProcessorIdOrderKey])>=(1) AND datalength([StateProcessorIdOrderKey])<=(769))"),
            Check("CK_MachineStateActivityAuthority_Position_UInt64Positive", "([Position]>=(1) AND [Position]<=(18446744073709551615.))"),
            Check("CK_MachineStateActivityAuthority_MachineState", "([MachineState]>=(0) AND [MachineState]<=(4))"),
            Check("CK_MachineStateActivityAuthority_ActiveState", "([ActiveState] IS NULL OR [ActiveState]>=(0) AND [ActiveState]<=(4))"),
            Check("CK_MachineStateActivityAuthority_ActivePair", "([ActiveState] IS NULL AND [ActiveStartedAt] IS NULL OR [ActiveState] IS NOT NULL AND [ActiveStartedAt] IS NOT NULL)"),
            Check("CK_MachineStateActivityAuthority_LastConsumedInstanceId_UInt64", "([LastConsumedInstanceId]>=(0) AND [LastConsumedInstanceId]<=(18446744073709551615.))"),
            Check("CK_MachineStateActivityAuthority_ContinuityPolicyIdentity", "(datalength([ContinuityPolicyIdentity])>(0))"),
            Check("CK_MachineStateActivityAuthority_ContinuityPolicyVersion", "(datalength([ContinuityPolicyVersion])>(0))"),
            Check("CK_MachineStateActivityAuthority_ProjectionRevision_UInt64", "([ProjectionRevision]>=(0) AND [ProjectionRevision]<=(18446744073709551615.))")
        ]);

    private static SqlTableDescriptor MachineStateActivitySignal() => Table(
        "MachineStateActivitySignal",
        [
            Column("MachineId", "uniqueidentifier"), Column("StreamKeyBinary", "varbinary", 512), Column("StateProcessorIdOrderKey", "varbinary", 769),
            Column("SignalOrdinal", "int"), ColumnMax("SignalKey", "nvarchar", collation: BinaryCollation), Column("SignalType", "tinyint"),
            Column("DigitalValue", "bit", isNullable: true), Column("DecimalValue", "nvarchar", 64, true, BinaryCollation), Decimal("CounterValue", 20, 0, true),
            Column("WholeNumberValue", "bigint", isNullable: true), ColumnMax("TextValue", "nvarchar", true, BinaryCollation), DateTimeOffset("TimestampValue", 7, true),
            ColumnMax("Source", "nvarchar", true, BinaryCollation), Column("Quality", "tinyint"), DateTimeOffset("Timestamp", 7)
        ],
        PrimaryKey("PK_MachineStateActivitySignal", "MachineId", "StreamKeyBinary", "StateProcessorIdOrderKey", "SignalOrdinal"),
        foreignKeys: [ForeignKey("FK_MachineStateActivitySignal_Authority", ["MachineId", "StreamKeyBinary", "StateProcessorIdOrderKey"], "MachineStateActivityAuthority", ["MachineId", "StreamKeyBinary", "StateProcessorIdOrderKey"])],
        checks:
        [
            Check("CK_MachineStateActivitySignal_Ordinal", "([SignalOrdinal]>=(0))"), Check("CK_MachineStateActivitySignal_SignalKey", "(datalength([SignalKey])>(0))"),
            Check("CK_MachineStateActivitySignal_SignalType", "([SignalType]>=(0) AND [SignalType]<=(7))"), Check("CK_MachineStateActivitySignal_Quality", "([Quality]>=(0) AND [Quality]<=(2))"),
            Check("CK_MachineStateActivitySignal_CounterValue_UInt64", "([CounterValue] IS NULL OR [CounterValue]>=(0) AND [CounterValue]<=(18446744073709551615.))"),
            Check("CK_MachineStateActivitySignal_ValueShape", "([DigitalValue] IS NULL AND [DecimalValue] IS NULL AND [CounterValue] IS NULL AND [WholeNumberValue] IS NULL AND [TextValue] IS NULL AND [TimestampValue] IS NULL OR [SignalType]=(0) AND [DigitalValue] IS NOT NULL AND [DecimalValue] IS NULL AND [CounterValue] IS NULL AND [WholeNumberValue] IS NULL AND [TextValue] IS NULL AND [TimestampValue] IS NULL OR ([SignalType]=(4) OR [SignalType]=(1)) AND [DigitalValue] IS NULL AND [DecimalValue] IS NOT NULL AND [CounterValue] IS NULL AND [WholeNumberValue] IS NULL AND [TextValue] IS NULL AND [TimestampValue] IS NULL OR [SignalType]=(2) AND [DigitalValue] IS NULL AND [DecimalValue] IS NULL AND [CounterValue] IS NOT NULL AND [WholeNumberValue] IS NULL AND [TextValue] IS NULL AND [TimestampValue] IS NULL OR [SignalType]=(3) AND [DigitalValue] IS NULL AND [DecimalValue] IS NULL AND [CounterValue] IS NULL AND [WholeNumberValue] IS NOT NULL AND [TextValue] IS NULL AND [TimestampValue] IS NULL OR ([SignalType]=(6) OR [SignalType]=(5)) AND [DigitalValue] IS NULL AND [DecimalValue] IS NULL AND [CounterValue] IS NULL AND [WholeNumberValue] IS NULL AND [TextValue] IS NOT NULL AND [TimestampValue] IS NULL OR [SignalType]=(7) AND [DigitalValue] IS NULL AND [DecimalValue] IS NULL AND [CounterValue] IS NULL AND [WholeNumberValue] IS NULL AND [TextValue] IS NULL AND [TimestampValue] IS NOT NULL)")
        ]);

    private static SqlTableDescriptor MachineStateChangeHistory() => HistoryTable("MachineStateChangeHistory", true);
    private static SqlTableDescriptor MachineActivityPeriodHistory() => HistoryTable("MachineActivityPeriodHistory", false);

    private static SqlTableDescriptor HistoryTable(string name, bool stateChange)
    {
        var columns = stateChange
            ? new[] { Column("MachineId", "uniqueidentifier"), Column("StreamKeyBinary", "varbinary", 512), Column("StateProcessorIdOrderKey", "varbinary", 769), Decimal("ProjectionRevision",20,0), Column("OutputOrdinal","int"), Decimal("Position",20,0), Column("PreviousState","tinyint"), Column("CurrentState","tinyint"), DateTimeOffset("OccurredAt",7) }.ToImmutableArray()
            : new[] { Column("MachineId", "uniqueidentifier"), Column("StreamKeyBinary", "varbinary", 512), Column("StateProcessorIdOrderKey", "varbinary", 769), Decimal("ProjectionRevision",20,0), Column("OutputOrdinal","int"), Decimal("Position",20,0), Column("MachineState","tinyint"), DateTimeOffset("StartedAt",7), DateTimeOffset("EndedAt",7) }.ToImmutableArray();
        var checks = stateChange
            ? new[] { Check($"CK_{name}_ProjectionRevision_UInt64","([ProjectionRevision]>=(0) AND [ProjectionRevision]<=(18446744073709551615.))"), Check($"CK_{name}_OutputOrdinal","([OutputOrdinal]>=(0))"), Check($"CK_{name}_Position_UInt64Positive","([Position]>=(1) AND [Position]<=(18446744073709551615.))"), Check($"CK_{name}_PreviousState","([PreviousState]>=(0) AND [PreviousState]<=(4))"), Check($"CK_{name}_CurrentState","([CurrentState]>=(0) AND [CurrentState]<=(4))") }.ToImmutableArray()
            : new[] { Check($"CK_{name}_ProjectionRevision_UInt64","([ProjectionRevision]>=(0) AND [ProjectionRevision]<=(18446744073709551615.))"), Check($"CK_{name}_OutputOrdinal","([OutputOrdinal]>=(0))"), Check($"CK_{name}_Position_UInt64Positive","([Position]>=(1) AND [Position]<=(18446744073709551615.))"), Check($"CK_{name}_MachineState","([MachineState]>=(0) AND [MachineState]<=(4))"), Check($"CK_{name}_Interval","([EndedAt]>=[StartedAt])") }.ToImmutableArray();
        var indexes = stateChange ? [] : new[] { Index("IX_MachineActivityPeriodHistory_StreamPosition", ["MachineId","StreamKeyBinary","Position","StateProcessorIdOrderKey","ProjectionRevision","OutputOrdinal"], ["MachineState","StartedAt","EndedAt"]) }.ToImmutableArray();
        return new SqlTableDescriptor(new("dbo", name), columns, PrimaryKey($"PK_{name}", "MachineId","StreamKeyBinary","StateProcessorIdOrderKey","ProjectionRevision","OutputOrdinal"), [], [ForeignKey($"FK_{name}_Authority", ["MachineId","StreamKeyBinary","StateProcessorIdOrderKey"], "MachineStateActivityAuthority", ["MachineId","StreamKeyBinary","StateProcessorIdOrderKey"])], checks, indexes);
    }

    private static SqlTableDescriptor Table(string name, ImmutableArray<SqlColumnDescriptor> columns, SqlPrimaryKeyDescriptor pk, ImmutableArray<SqlForeignKeyDescriptor> foreignKeys = default, ImmutableArray<SqlCheckConstraintDescriptor> checks = default) => new(new("dbo",name), columns, pk, [], foreignKeys.IsDefault ? [] : foreignKeys, checks.IsDefault ? [] : checks, []);
    private static SqlColumnDescriptor Column(string name,string type,int? length=null,bool isNullable=false,string? collation=null)=>new(name,type,length is null?null:SqlLengthDescriptor.Bounded(length.Value),null,null,isNullable,collation,null);
    private static SqlColumnDescriptor ColumnMax(string name,string type,bool isNullable=false,string? collation=null)=>new(name,type,SqlLengthDescriptor.Max,null,null,isNullable,collation,null);
    private static SqlColumnDescriptor Decimal(string name,byte precision,byte scale,bool isNullable=false)=>new(name,"decimal",null,precision,scale,isNullable,null,null);
    private static SqlColumnDescriptor DateTimeOffset(string name,byte scale,bool isNullable=false)=>new(name,"datetimeoffset",null,null,scale,isNullable,null,null);
    private static SqlPrimaryKeyDescriptor PrimaryKey(string name,params string[] columns)=>new(name,Structure(true,columns,[]));
    private static SqlForeignKeyDescriptor ForeignKey(string name,string[] columns,string table,string[] referenced)=>new(name,columns.ToImmutableArray(),new("dbo",table),referenced.ToImmutableArray(),SqlReferentialAction.NoAction,SqlReferentialAction.NoAction,true,true,false);
    private static SqlCheckConstraintDescriptor Check(string name,string definition)=>new(name,definition,true,true,false);
    private static SqlIndexDescriptor Index(string name,string[] columns,string[] included)=>new(name,false,true,Structure(false,columns,included));
    private static SqlIndexStructureDescriptor Structure(bool clustered,IEnumerable<string> columns,IEnumerable<string> included)=>new(clustered,columns.Select((c,i)=>new SqlIndexColumnDescriptor(c,SqlIndexColumnDirection.Ascending,i+1)).ToImmutableArray(),included.ToImmutableArray(),null);
}
