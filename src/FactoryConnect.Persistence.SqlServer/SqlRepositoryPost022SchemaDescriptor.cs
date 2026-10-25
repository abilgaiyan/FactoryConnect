namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost022SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new(
        [.. previous.Tables.Select(table => table.Name == new SqlObjectName("dbo", "ProductionContextProcessor")
            ? table with
            {
                Columns = [.. table.Columns,
                    new SqlColumnDescriptor("AdmissionBound", "bit", null, null, null, false, null, null),
                    new SqlColumnDescriptor("ProductionStartsAt", "datetimeoffset", null, null, 7, true, null, null)],
                CheckConstraints = [.. table.CheckConstraints,
                    new SqlCheckConstraintDescriptor("CK_ProductionContextProcessor_AdmissionBinding",
                        "([AdmissionBound]=(1) OR [ProductionStartsAt] IS NULL)", true, true, false)],
                DefaultConstraints = [.. (table.DefaultConstraints.IsDefault ? [] : table.DefaultConstraints),
                    new SqlDefaultConstraintDescriptor("DF_ProductionContextProcessor_AdmissionBound", "AdmissionBound", "((0))")],
            }
            : table)]);
}
