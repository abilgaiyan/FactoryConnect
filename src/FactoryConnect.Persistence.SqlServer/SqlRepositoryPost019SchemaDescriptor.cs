namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost019SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post018)
    {
        ArgumentNullException.ThrowIfNull(post018);
        return new SqlSchemaDescriptor([.. post018.Tables, Table("AssumedBreakConfigurationSnapshot"), Table("AssumedBreakConfigurationAdmission")]);
    }
    private static SqlTableDescriptor Table(string name) => new(new SqlObjectName("dbo", name),
        [Column("IdentityHash", "binary", SqlLengthDescriptor.Bounded(32)),
            .. (name.EndsWith("Admission", StringComparison.Ordinal) ? new[] { Column("SnapshotIdentityHash", "binary", SqlLengthDescriptor.Bounded(32)) } : []),
            Column("IdentityCodecVersion", "int"),
            Column("IdentityBinary", "varbinary", SqlLengthDescriptor.Max), Column("PayloadCodecVersion", "int"),
            Column("ContentHash", "binary", SqlLengthDescriptor.Bounded(32)), Column("PayloadBinary", "varbinary", SqlLengthDescriptor.Max)],
        new SqlPrimaryKeyDescriptor("PK_" + name, new SqlIndexStructureDescriptor(true,
            [new SqlIndexColumnDescriptor("IdentityHash", SqlIndexColumnDirection.Ascending, 1)], [], null)), [],
        name.EndsWith("Admission", StringComparison.Ordinal)
            ? [new SqlForeignKeyDescriptor("FK_AssumedBreakConfigurationAdmission_Snapshot", ["SnapshotIdentityHash"],
                new SqlObjectName("dbo", "AssumedBreakConfigurationSnapshot"), ["IdentityHash"],
                SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false)] : [],
        [Check("CK_" + name + "_IdentityVersion", "([IdentityCodecVersion]>(0))"),
            Check("CK_" + name + "_PayloadVersion", "([PayloadCodecVersion]>(0))"),
            Check("CK_" + name + "_IdentitySize", "(datalength([IdentityBinary])>=(1) AND datalength([IdentityBinary])<=(1048576))"),
            Check("CK_" + name + "_PayloadSize", "(datalength([PayloadBinary])>=(1) AND datalength([PayloadBinary])<=(1048576))")],
        name.EndsWith("Admission", StringComparison.Ordinal)
            ? [new SqlIndexDescriptor("IX_AssumedBreakConfigurationAdmission_Snapshot", false, true,
                new SqlIndexStructureDescriptor(false, [new SqlIndexColumnDescriptor("SnapshotIdentityHash", SqlIndexColumnDirection.Ascending, 1)], [], null))] : []);
    private static SqlColumnDescriptor Column(string name, string type, SqlLengthDescriptor? length = null) => new(name, type, length, null, null, false, null, null);
    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(name, definition, true, true, false);
}
