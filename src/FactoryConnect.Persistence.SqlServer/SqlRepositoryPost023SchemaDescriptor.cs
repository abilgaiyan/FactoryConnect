namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost023SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([.. previous.Tables,
        new SqlTableDescriptor(new("dbo", "ProductionAdmissionRecoveryAudit"),
            [new("OperationId", "uniqueidentifier", null, null, null, false, null, null),
             new("ProductionContextProcessorRowId", "bigint", null, null, null, false, null, null),
             new("SchemaVersion", "int", null, null, null, false, null, null),
             new("BeforeBoundary", "datetimeoffset", null, null, 7, true, null, null),
             new("AfterBoundary", "datetimeoffset", null, null, 7, false, null, null),
             new("RecordedAtUtc", "datetimeoffset", null, null, 7, false, null, null),
             new("Evidence", "nvarchar", SqlLengthDescriptor.Max, null, null, false, "Latin1_General_100_BIN2", null)],
            new("PK_ProductionAdmissionRecoveryAudit", new(true, [K("OperationId")], [], null)),
            [new("UQ_ProductionAdmissionRecoveryAudit_Processor", new(false, [K("ProductionContextProcessorRowId")], [], null))],
            [], [], [])]);

    private static SqlIndexColumnDescriptor K(string name) => new(name, SqlIndexColumnDirection.Ascending, 1);
}
