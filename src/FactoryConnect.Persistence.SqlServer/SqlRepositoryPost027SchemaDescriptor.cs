namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost027SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "SupervisorProductionEntryRevision"),
            [
                C("RevisionId", "uniqueidentifier"), C("EntryId", "uniqueidentifier"),
                new("PreviousRevisionId", "uniqueidentifier", null, null, null, true, null, null),
                C("RevisionNumber", "int"), N("ProductionOrder", 200), N("PartNumber", 200),
                N("Operation", 200), new("Note", "nvarchar", SqlLengthDescriptor.Bounded(2000), null, null, true, BinaryCollation, null),
                new("GoodQuantity", "bigint", null, null, null, true, null, null),
                new("RejectedQuantity", "bigint", null, null, null, true, null, null),
                N("CorrectionReason", 2000), N("RevisedBy", 200),
                new("RevisedAtUtc", "datetimeoffset", null, null, 7, false, null, null)
            ],
            new("PK_SupervisorProductionEntryRevision", new(true, [K("RevisionId", 1)], [], null)),
            [new("UQ_SupervisorProductionEntryRevision_EntryNumber",
                new(false, [K("EntryId", 1), K("RevisionNumber", 2)], [], null))],
            [new("FK_SupervisorProductionEntryRevision_Entry", ["EntryId"],
                new("dbo", "SupervisorProductionEntry"), ["EntryId"],
                SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false)],
            [
                new("CK_SupervisorProductionEntryRevision_Number", "([RevisionNumber]>(0))", true, true, false),
                new("CK_SupervisorProductionEntryRevision_Quantity",
                    "([GoodQuantity] IS NULL AND [RejectedQuantity] IS NULL OR [GoodQuantity] IS NOT NULL AND [RejectedQuantity] IS NOT NULL AND [GoodQuantity]>=(0) AND [RejectedQuantity]>=(0))", true, true, false),
                new("CK_SupervisorProductionEntryRevision_Required",
                    "(len(Trim([ProductionOrder]))>(0) AND len(Trim([PartNumber]))>(0) AND len(Trim([Operation]))>(0) AND len(Trim([CorrectionReason]))>(0) AND len(Trim([RevisedBy]))>(0))", true, true, false),
                new("CK_SupervisorProductionEntryRevision_Utc", "(datepart(tzoffset,[RevisedAtUtc])=(0))", true, true, false)
            ], [])]);

    private static SqlColumnDescriptor C(string name, string type) =>
        new(name, type, null, null, null, false, null, null);

    private static SqlColumnDescriptor N(string name, int length) =>
        new(name, "nvarchar", SqlLengthDescriptor.Bounded(length), null, null, false, BinaryCollation, null);

    private static SqlIndexColumnDescriptor K(string name, int ordinal) =>
        new(name, SqlIndexColumnDirection.Ascending, ordinal);
}
