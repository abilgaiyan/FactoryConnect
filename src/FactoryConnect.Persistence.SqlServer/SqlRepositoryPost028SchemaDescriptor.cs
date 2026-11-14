namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost028SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "SupervisorProductionEntryRevisionReview"),
            [
                new("RevisionId", "uniqueidentifier", null, null, null, false, null, null),
                new("DecisionId", "uniqueidentifier", null, null, null, false, null, null),
                N("Decision", 20),
                N("Reason", 2000),
                N("ReviewedBy", 200),
                new("ReviewedAtUtc", "datetimeoffset", null, null, 7, false, null, null)
            ],
            new("PK_SupervisorProductionEntryRevisionReview", new(true, [K("RevisionId")], [], null)),
            [new("UQ_SupervisorProductionEntryRevisionReview_DecisionId", new(false, [K("DecisionId")], [], null))],
            [new("FK_SupervisorProductionEntryRevisionReview_Entry", ["RevisionId"],
                new("dbo", "SupervisorProductionEntryRevision"), ["RevisionId"],
                SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false)],
            [
                new("CK_SupervisorProductionEntryRevisionReview_Decision",
                    "([Decision]=N'Rejected' OR [Decision]=N'Approved')", true, true, false),
                new("CK_SupervisorProductionEntryRevisionReview_Reason",
                    "(len(Trim([Reason]))>(0))", true, true, false),
                new("CK_SupervisorProductionEntryRevisionReview_Reviewer",
                    "(len(Trim([ReviewedBy]))>(0))", true, true, false),
                new("CK_SupervisorProductionEntryRevisionReview_Utc",
                    "(datepart(tzoffset,[ReviewedAtUtc])=(0))", true, true, false)
            ], [])]);

    private static SqlColumnDescriptor N(string name, int length) =>
        new(name, "nvarchar", SqlLengthDescriptor.Bounded(length), null, null, false, BinaryCollation, null);

    private static SqlIndexColumnDescriptor K(string name) =>
        new(name, SqlIndexColumnDirection.Ascending, 1);
}
