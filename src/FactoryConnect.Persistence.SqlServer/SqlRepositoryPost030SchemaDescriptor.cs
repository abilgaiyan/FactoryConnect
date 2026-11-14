namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost030SchemaDescriptor
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "SupervisorDowntimeExplanation"),
            [
                Id("ExplanationId"), Id("MachineId"), N("StateProcessorId", 200),
                BigInt("ObservationPosition"), Dt("ActivityStartsAtUtc"), Dt("ActivityEndsAtUtc"),
                N("ReasonCode", 100), N("Explanation", 2000), N("EnteredBy", 200), Dt("EnteredAtUtc")
            ],
            Pk("PK_SupervisorDowntimeExplanation", "ExplanationId"),
            [new("UQ_SupervisorDowntimeExplanation_Period", new(false,
                [K("MachineId", 1), K("StateProcessorId", 2), K("ObservationPosition", 3)], [], null))],
            [],
            [
                C("CK_SupervisorDowntimeExplanation_Position", "([ObservationPosition]>=(0))"),
                C("CK_SupervisorDowntimeExplanation_Interval", "([ActivityStartsAtUtc]<[ActivityEndsAtUtc])"),
                C("CK_SupervisorDowntimeExplanation_Required",
                    "(len(Trim([StateProcessorId]))>(0) AND len(Trim([ReasonCode]))>(0) AND len(Trim([Explanation]))>(0) AND len(Trim([EnteredBy]))>(0))"),
                C("CK_SupervisorDowntimeExplanation_Utc",
                    "(datepart(tzoffset,[ActivityStartsAtUtc])=(0) AND datepart(tzoffset,[ActivityEndsAtUtc])=(0) AND datepart(tzoffset,[EnteredAtUtc])=(0))")
            ], []),
        new SqlTableDescriptor(new("dbo", "SupervisorDowntimeExplanationReview"),
            [
                Id("ExplanationId"), Id("DecisionId"), N("Decision", 20),
                N("Reason", 2000), N("ReviewedBy", 200), Dt("ReviewedAtUtc")
            ],
            Pk("PK_SupervisorDowntimeExplanationReview", "ExplanationId"),
            [new("UQ_SupervisorDowntimeExplanationReview_DecisionId",
                new(false, [K("DecisionId", 1)], [], null))],
            [new("FK_SupervisorDowntimeExplanationReview_Explanation", ["ExplanationId"],
                new("dbo", "SupervisorDowntimeExplanation"), ["ExplanationId"],
                SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false)],
            [
                C("CK_SupervisorDowntimeExplanationReview_Decision",
                    "([Decision]=N'Rejected' OR [Decision]=N'Approved')"),
                C("CK_SupervisorDowntimeExplanationReview_Required",
                    "(len(Trim([Reason]))>(0) AND len(Trim([ReviewedBy]))>(0))"),
                C("CK_SupervisorDowntimeExplanationReview_Utc",
                    "(datepart(tzoffset,[ReviewedAtUtc])=(0))")
            ], [])
    ]);

    private static SqlColumnDescriptor Id(string name) =>
        new(name, "uniqueidentifier", null, null, null, false, null, null);
    private static SqlColumnDescriptor BigInt(string name) =>
        new(name, "bigint", null, null, null, false, null, null);
    private static SqlColumnDescriptor Dt(string name) =>
        new(name, "datetimeoffset", null, null, 7, false, null, null);
    private static SqlColumnDescriptor N(string name, int length) =>
        new(name, "nvarchar", SqlLengthDescriptor.Bounded(length), null, null, false, BinaryCollation, null);
    private static SqlPrimaryKeyDescriptor Pk(string name, string column) =>
        new(name, new(true, [K(column, 1)], [], null));
    private static SqlIndexColumnDescriptor K(string name, int ordinal) =>
        new(name, SqlIndexColumnDirection.Ascending, ordinal);
    private static SqlCheckConstraintDescriptor C(string name, string definition) =>
        new(name, definition, true, true, false);
}
