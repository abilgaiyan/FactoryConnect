namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost017SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post016)
    {
        ArgumentNullException.ThrowIfNull(post016);
        return new SqlSchemaDescriptor([.. post016.Tables, Table("DerivedOeeAssessment")]);
    }
    private static SqlTableDescriptor Table(string name) => new(new SqlObjectName("dbo", name),
        [Column("IdentityHash", "binary", SqlLengthDescriptor.Bounded(32)), Column("IdentityCodecVersion", "int"),
            Column("IdentityBinary", "varbinary", SqlLengthDescriptor.Max), Column("PayloadCodecVersion", "int"),
            Column("ContentHash", "binary", SqlLengthDescriptor.Bounded(32)), Column("PayloadBinary", "varbinary", SqlLengthDescriptor.Max)],
        new SqlPrimaryKeyDescriptor("PK_" + name, new SqlIndexStructureDescriptor(true,
            [new SqlIndexColumnDescriptor("IdentityHash", SqlIndexColumnDirection.Ascending, 1)], [], null)), [], [],
        [Check("CK_" + name + "_IdentityVersion", "([IdentityCodecVersion]>(0))"),
            Check("CK_" + name + "_PayloadVersion", "([PayloadCodecVersion]>(0))"),
            Check("CK_" + name + "_IdentitySize", "(datalength([IdentityBinary])>=(1) AND datalength([IdentityBinary])<=(16777216))"),
            Check("CK_" + name + "_PayloadSize", "(datalength([PayloadBinary])>=(1) AND datalength([PayloadBinary])<=(16777216))")], []);
    private static SqlColumnDescriptor Column(string name, string type, SqlLengthDescriptor? length = null) => new(name, type, length, null, null, false, null, null);
    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(name, definition, true, true, false);
}
