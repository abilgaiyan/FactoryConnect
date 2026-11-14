namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost031SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "SupervisorDowntimeReason"),
            [
                new("ReasonCode", "nvarchar", SqlLengthDescriptor.Bounded(100), null, null, false, "Latin1_General_100_BIN2", null),
                new("DisplayName", "nvarchar", SqlLengthDescriptor.Bounded(200), null, null, false, null, null),
                new("Description", "nvarchar", SqlLengthDescriptor.Bounded(1000), null, null, true, null, null),
                new("IsActive", "bit", null, null, null, false, null, null),
                new("CreatedAtUtc", "datetimeoffset", null, null, 7, false, null, null),
                new("UpdatedAtUtc", "datetimeoffset", null, null, 7, false, null, null)
            ],
            new("PK_SupervisorDowntimeReason", new(true,
                [new("ReasonCode", SqlIndexColumnDirection.Ascending, 1)], [], null)),
            [], [],
            [
                new("CK_SupervisorDowntimeReason_Code", "(len(ltrim(rtrim([ReasonCode])))>(0))", true, true, false),
                new("CK_SupervisorDowntimeReason_Name", "(len(ltrim(rtrim([DisplayName])))>(0))", true, true, false),
                new("CK_SupervisorDowntimeReason_Utc",
                    "(datepart(tzoffset,[CreatedAtUtc])=(0) AND datepart(tzoffset,[UpdatedAtUtc])=(0))",
                    true, true, false)
            ], [])
    ]);
}
