using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost015SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post014)
    {
        ArgumentNullException.ThrowIfNull(post014);
        return new SqlSchemaDescriptor(
        [
            .. post014.Tables,
            Table("OperationalMetricCoverageSubject",
                [new SqlColumnDescriptor("SubjectId", "bigint", null, null, null, false, null, new SqlIdentityDescriptor(1, 1, false)),
                    Column("SubjectHash", "binary", SqlLengthDescriptor.Bounded(32)), Column("IdentityCodecVersion", "int"),
                    Column("SubjectBinary", "varbinary", SqlLengthDescriptor.Max),
                    Column("MetricAggregationProcessorRowId", "bigint"), Column("MetricInputStreamRowId", "bigint"),
                    new SqlColumnDescriptor("SourcePosition", "decimal", null, 20, 0, false, null, null)],
                PrimaryKey("PK_OperationalMetricCoverageSubject", "SubjectId"),
                [new SqlUniqueConstraintDescriptor("UQ_OperationalMetricCoverageSubject_Hash", true, Structure(false, "SubjectHash"))],
                [ForeignKey("FK_OperationalMetricCoverageSubject_ProcessorStream", ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"], "MetricAggregationProcessor", ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"]),
                    ForeignKey("FK_OperationalMetricCoverageSubject_SourceRevision", ["MetricAggregationProcessorRowId", "SourcePosition"], "MetricAggregationRevision", ["MetricAggregationProcessorRowId", "Position"])],
                [Check("CK_OperationalMetricCoverageSubject_Codec", "([IdentityCodecVersion]>(0))"),
                    Check("CK_OperationalMetricCoverageSubject_Binary", "(datalength([SubjectBinary])>(0))"),
                    Check("CK_OperationalMetricCoverageSubject_Position", "([SourcePosition]>=(1) AND [SourcePosition]<=(18446744073709551615.))")]),
            Table("OperationalMetricCoverageVersion",
                [Column("SubjectId", "bigint"), Column("AssessmentRevision", "bigint"), Column("ContentCodecVersion", "int"),
                    Column("ContentBinary", "varbinary", SqlLengthDescriptor.Max)],
                PrimaryKey("PK_OperationalMetricCoverageVersion", "SubjectId", "AssessmentRevision"), [],
                [ForeignKey("FK_OperationalMetricCoverageVersion_Subject", ["SubjectId"], "OperationalMetricCoverageSubject", ["SubjectId"])],
                [Check("CK_OperationalMetricCoverageVersion_Revision", "([AssessmentRevision]>(0))"),
                    Check("CK_OperationalMetricCoverageVersion_Codec", "([ContentCodecVersion]>(0))"),
                    Check("CK_OperationalMetricCoverageVersion_Binary", "(datalength([ContentBinary])>(0))")]),
            Table("OperationalMetricCoverageHead",
                [Column("SubjectId", "bigint"), Column("HeadRevision", "bigint")],
                PrimaryKey("PK_OperationalMetricCoverageHead", "SubjectId"), [],
                [ForeignKey("FK_OperationalMetricCoverageHead_Version", ["SubjectId", "HeadRevision"], "OperationalMetricCoverageVersion", ["SubjectId", "AssessmentRevision"])],
                [Check("CK_OperationalMetricCoverageHead_Revision", "([HeadRevision]>(0))")])
        ]);
    }

    private static SqlColumnDescriptor Column(string name, string type, SqlLengthDescriptor? length = null) => new(name, type, length, null, null, false, null, null);
    private static SqlTableDescriptor Table(string name, ImmutableArray<SqlColumnDescriptor> columns,
        SqlPrimaryKeyDescriptor primaryKey, ImmutableArray<SqlUniqueConstraintDescriptor> uniques,
        ImmutableArray<SqlForeignKeyDescriptor> foreignKeys, ImmutableArray<SqlCheckConstraintDescriptor> checks) =>
        new(new SqlObjectName("dbo", name), columns, primaryKey, uniques, foreignKeys, checks, []);
    private static SqlIndexStructureDescriptor Structure(bool clustered, params string[] columns) =>
        new(clustered, columns.Select(static (column, index) => new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1)).ToImmutableArray(), [], null);
    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) => new(name, Structure(true, columns));
    private static SqlForeignKeyDescriptor ForeignKey(string name, string[] columns, string table, string[] targets) =>
        new(name, columns.ToImmutableArray(), new SqlObjectName("dbo", table), targets.ToImmutableArray(), SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false);
    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(name, definition, true, true, false);
}
