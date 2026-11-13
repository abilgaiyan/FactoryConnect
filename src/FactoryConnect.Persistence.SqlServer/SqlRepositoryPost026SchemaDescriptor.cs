namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost026SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "SupervisorProductionEntryReview"),
            [
                new("EntryId", "uniqueidentifier", null, null, null, false, null, null),
                new("DecisionId", "uniqueidentifier", null, null, null, false, null, null),
                N("Decision", 20),
                N("Reason", 2000),
                N("ReviewedBy", 200),
                new("ReviewedAtUtc", "datetimeoffset", null, null, 7, false, null, null)
            ],
            new("PK_SupervisorProductionEntryReview", new(true, [K("EntryId")], [], null)),
            [new("UQ_SupervisorProductionEntryReview_DecisionId", new(false, [K("DecisionId")], [], null))],
            [new("FK_SupervisorProductionEntryReview_Entry", ["EntryId"],
                new("dbo", "SupervisorProductionEntry"), ["EntryId"],
                SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false)],
            [
                new("CK_SupervisorProductionEntryReview_Decision",
                    "([Decision]=N'Rejected' OR [Decision]=N'Approved')", true, true, false),
                new("CK_SupervisorProductionEntryReview_Reason",
                    "(len(Trim([Reason]))>(0))", true, true, false),
                new("CK_SupervisorProductionEntryReview_Reviewer",
                    "(len(Trim([ReviewedBy]))>(0))", true, true, false),
                new("CK_SupervisorProductionEntryReview_Utc",
                    "(datepart(tzoffset,[ReviewedAtUtc])=(0))", true, true, false)
            ], [])]);

    private static SqlColumnDescriptor N(string name, int length) =>
        new(name, "nvarchar", SqlLengthDescriptor.Bounded(length), null, null, false, BinaryCollation, null);

    private static SqlIndexColumnDescriptor K(string name) =>
        new(name, SqlIndexColumnDirection.Ascending, 1);
}
