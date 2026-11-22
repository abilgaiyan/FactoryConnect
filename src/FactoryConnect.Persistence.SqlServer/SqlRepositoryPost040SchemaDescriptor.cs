namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost040SchemaDescriptor
{
    private const string Bin = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "MachineProductionEvidenceProfile"),
            [
                new("ProfileRevisionId", "uniqueidentifier", null, null, null, false, null, null),
                new("MachineId", "uniqueidentifier", null, null, null, false, null, null),
                new("RevisionNumber", "int", null, null, null, false, null, null),
                new("EvidenceMode", "nvarchar", SqlLengthDescriptor.Bounded(30), null, null, false, Bin, null),
                new("StateDataItemId", "nvarchar", SqlLengthDescriptor.Bounded(200), null, null, true, Bin, null),
                new("CounterDataItemId", "nvarchar", SqlLengthDescriptor.Bounded(200), null, null, true, Bin, null),
                new("CounterSemantics", "nvarchar", SqlLengthDescriptor.Bounded(30), null, null, true, Bin, null),
                new("EffectiveFromUtc", "datetimeoffset", null, null, 7, false, null, null),
                new("ConfiguredBy", "nvarchar", SqlLengthDescriptor.Bounded(200), null, null, false, Bin, null),
                new("ConfiguredAtUtc", "datetimeoffset", null, null, 7, false, null, null)
            ],
            new("PK_MachineProductionEvidenceProfile", new(true,
                [new("ProfileRevisionId", SqlIndexColumnDirection.Ascending, 1)], [], null)),
            [
                new("UQ_MachineProductionEvidenceProfile_Revision", new(false,
                    [new("MachineId", SqlIndexColumnDirection.Ascending, 1),
                     new("RevisionNumber", SqlIndexColumnDirection.Ascending, 2)], [], null)),
                new("UQ_MachineProductionEvidenceProfile_Effective", new(false,
                    [new("MachineId", SqlIndexColumnDirection.Ascending, 1),
                     new("EffectiveFromUtc", SqlIndexColumnDirection.Ascending, 2)], [], null))
            ], [],
            [
                new("CK_MachineProductionEvidenceProfile_Identity",
                    "([MachineId]<>('00000000-0000-0000-0000-000000000000') AND [ProfileRevisionId]<>('00000000-0000-0000-0000-000000000000') AND [RevisionNumber]>(0))", true, true, false),
                new("CK_MachineProductionEvidenceProfile_Mode",
                    "([EvidenceMode]=N'CounterOnly' OR [EvidenceMode]=N'StateOnly' OR [EvidenceMode]=N'CncCounterAndState')", true, true, false),
                new("CK_MachineProductionEvidenceProfile_Signals",
                    "([EvidenceMode]=N'StateOnly' AND [StateDataItemId] IS NOT NULL AND [CounterDataItemId] IS NULL AND [CounterSemantics] IS NULL OR [EvidenceMode]=N'CounterOnly' AND [StateDataItemId] IS NULL AND [CounterDataItemId] IS NOT NULL AND [CounterSemantics] IS NOT NULL OR [EvidenceMode]=N'CncCounterAndState' AND [StateDataItemId] IS NOT NULL AND [CounterDataItemId] IS NOT NULL AND [CounterSemantics] IS NOT NULL)", true, true, false),
                new("CK_MachineProductionEvidenceProfile_Counter",
                    "([CounterSemantics] IS NULL OR [CounterSemantics]=N'CyclePulse' OR [CounterSemantics]=N'CumulativeMonotonic' OR [CounterSemantics]=N'CumulativeResettable')", true, true, false),
                new("CK_MachineProductionEvidenceProfile_Required",
                    "(len(TRIM([ConfiguredBy]))>(0) AND ([StateDataItemId] IS NULL OR len(TRIM([StateDataItemId]))>(0)) AND ([CounterDataItemId] IS NULL OR len(TRIM([CounterDataItemId]))>(0)))", true, true, false),
                new("CK_MachineProductionEvidenceProfile_Utc",
                    "(datepart(tzoffset,[EffectiveFromUtc])=(0) AND datepart(tzoffset,[ConfiguredAtUtc])=(0))", true, true, false)
            ], [])
    ]);
}
