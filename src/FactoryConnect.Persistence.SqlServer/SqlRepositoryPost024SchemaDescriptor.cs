namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost024SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "SupervisorProductionEntry"),
            [
                C("EntryId", "uniqueidentifier"),
                C("MachineId", "uniqueidentifier"),
                N("ShiftScheduleAssignmentId", 200),
                N("ShiftId", 200),
                C("ShiftStartsAtUtc", "datetimeoffset", scale: 7),
                C("ShiftEndsAtUtc", "datetimeoffset", scale: 7),
                N("ProductionOrder", 200),
                N("PartNumber", 200),
                N("Operation", 200),
                N("Note", 2000, nullable: true, binary: false),
                N("EnteredBy", 200),
                C("RecordedAtUtc", "datetimeoffset", scale: 7)
            ],
            new("PK_SupervisorProductionEntry", new(true, [K("EntryId", 1)], [], null)),
            [], [],
            [
                new("CK_SupervisorProductionEntry_ShiftBounds",
                    "([ShiftEndsAtUtc]>[ShiftStartsAtUtc])", true, true, false),
                new("CK_SupervisorProductionEntry_ShiftUtc",
                    "(datepart(tzoffset,[ShiftStartsAtUtc])=(0) AND datepart(tzoffset,[ShiftEndsAtUtc])=(0))", true, true, false),
                new("CK_SupervisorProductionEntry_RecordedUtc",
                    "(datepart(tzoffset,[RecordedAtUtc])=(0))", true, true, false),
                new("CK_SupervisorProductionEntry_RequiredText",
                    "(len(trim([ShiftScheduleAssignmentId]))>(0) AND len(trim([ShiftId]))>(0) AND len(trim([ProductionOrder]))>(0) AND len(trim([PartNumber]))>(0) AND len(trim([Operation]))>(0) AND len(trim([EnteredBy]))>(0))", true, true, false)
            ],
            [
                new("IX_SupervisorProductionEntry_MachineShift", false, true,
                    new(false, [K("MachineId", 1), K("ShiftStartsAtUtc", 2), K("ShiftEndsAtUtc", 3), K("RecordedAtUtc", 4)], [], null))
            ])]);

    private static SqlColumnDescriptor C(string name, string type, byte? scale = null) =>
        new(name, type, null, null, scale, false, null, null);

    private static SqlColumnDescriptor N(string name, int length, bool nullable = false, bool binary = true) =>
        new(name, "nvarchar", SqlLengthDescriptor.Bounded(length), null, null, nullable,
            binary ? BinaryCollation : null, null);

    private static SqlIndexColumnDescriptor K(string name, int ordinal) =>
        new(name, SqlIndexColumnDirection.Ascending, ordinal);
}
