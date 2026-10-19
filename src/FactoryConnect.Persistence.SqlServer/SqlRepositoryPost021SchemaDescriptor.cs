using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost021SchemaDescriptor
{
    private const string Policy = "N'availability-scope-containment/1'";
    private const string UInt64Maximum = "18446744073709551615.";
    private const string WitnessShape = "([ReasonCode] IS NOT NULL AND [ReasonCode]=(6) AND [ConflictWitnessId] IS NOT NULL OR ([ReasonCode] IS NULL OR [ReasonCode]<>(6)) AND [ConflictWitnessId] IS NULL)";

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new([
        .. previous.Tables.Select(Update), WitnessArchive(), ResultArchive(), WitnessReferences()
    ]);

    private static SqlTableDescriptor Update(SqlTableDescriptor table) => table.Name.ObjectName switch
    {
        "OperationalMetricProjectionProcessor" => table with
        {
            Columns = [.. table.Columns, C("PublicationFormatVersion", "smallint"), C("EvaluationPolicyId", "nvarchar", 64, true)],
            UniqueConstraints = [.. table.UniqueConstraints,
                Unique("UQ_OMPP_Format", "OperationalMetricProjectionProcessorRowId", "PublicationFormatVersion"),
                Unique("UQ_OMPP_Source", "OperationalMetricProjectionProcessorRowId", "MetricAggregationProcessorRowId", "MetricInputStreamRowId")],
            CheckConstraints = [.. table.CheckConstraints, Check("CK_OMPP_FormatPolicy",
                $"([PublicationFormatVersion]=(1) AND [EvaluationPolicyId] IS NULL OR [PublicationFormatVersion]=(2) AND [EvaluationPolicyId] IS NOT NULL AND [EvaluationPolicyId]={Policy})")],
            DefaultConstraints = [new("DF_OMPP_PublicationFormat", "PublicationFormatVersion", "((1))")]
        },
        "OperationalMetricProjection" => table with
        {
            Columns = [.. table.Columns, C("PublicationFormatVersion", "smallint"), C("EvaluationPolicyId", "nvarchar", 64, true),
                C("ContainmentResultId", "binary", 32, true), C("ConflictWitnessId", "binary", 32, true)],
            CheckConstraints = [.. table.CheckConstraints.Where(c => c.Name is not ("CK_OperationalMetricProjection_ReasonCode" or "CK_OperationalMetricProjection_StatusShape")),
                Check("CK_OperationalMetricProjection_ReasonCode", "([ReasonCode] IS NULL OR ([ReasonCode]=(6) OR [ReasonCode]=(5) OR [ReasonCode]=(4) OR [ReasonCode]=(3) OR [ReasonCode]=(2) OR [ReasonCode]=(1) OR [ReasonCode]=(0)))"),
                Check("CK_OperationalMetricProjection_StatusShape", "([Status]=(0) AND [MetricValue] IS NOT NULL AND datalength([MetricValue])>(0) AND [ReasonCode] IS NULL AND [ReasonOperandName] IS NULL OR ([Status]=(2) OR [Status]=(1)) AND [MetricValue] IS NULL AND [ReasonCode] IS NOT NULL AND ([ReasonCode]=(6) OR [ReasonCode]=(5) OR [ReasonCode]=(4) OR [ReasonCode]=(3) OR [ReasonCode]=(2) OR [ReasonCode]=(1) OR [ReasonCode]=(0)) AND ([ReasonOperandName] IS NULL OR datalength([ReasonOperandName])>(0)))"),
                Check("CK_OMP_FormatPolicy", $"([PublicationFormatVersion]=(1) AND [EvaluationPolicyId] IS NULL AND [ContainmentResultId] IS NULL AND [ConflictWitnessId] IS NULL AND ([ReasonCode] IS NULL OR ([ReasonCode]=(5) OR [ReasonCode]=(4) OR [ReasonCode]=(3) OR [ReasonCode]=(2) OR [ReasonCode]=(1) OR [ReasonCode]=(0))) OR [PublicationFormatVersion]=(2) AND [EvaluationPolicyId] IS NOT NULL AND [EvaluationPolicyId]={Policy} AND [ContainmentResultId] IS NOT NULL)"),
                Check("CK_OMP_WitnessShape", WitnessShape)],
            ForeignKeys = [.. table.ForeignKeys,
                Fk("FK_OMP_ProcessorFormat", ["OperationalMetricProjectionProcessorRowId", "PublicationFormatVersion"], "OperationalMetricProjectionProcessor", ["OperationalMetricProjectionProcessorRowId", "PublicationFormatVersion"]),
                Fk("FK_OMP_ContainmentResult", ["ContainmentResultId", "OperationalMetricProjectionProcessorRowId", "EvaluationKeyHash", "SourceRevisionPosition"], "OperationalMetricPublicationResultArchive", ["ResultId", "OperationalMetricProjectionProcessorRowId", "EvaluationKeyHash", "SourceRevisionPosition"]),
                Fk("FK_OMP_ConflictWitness", ["ConflictWitnessId"], "OperationalMetricConflictWitnessArchive", ["WitnessId"])],
            DefaultConstraints = [new("DF_OMP_PublicationFormat", "PublicationFormatVersion", "((1))")]
        },
        "OperationalMetricProjectionEvidence" => table with
        {
            CheckConstraints = [.. table.CheckConstraints.Select(c => c.Name == "CK_OperationalMetricProjectionEvidence_SubtypeShape"
                ? c with { CanonicalDefinition = c.CanonicalDefinition.Replace("[DependencySnapshotCodecVersion] = (1)", "([DependencySnapshotCodecVersion]=(2) OR [DependencySnapshotCodecVersion]=(1))", StringComparison.Ordinal) } : c)]
        },
        _ => table
    };

    private static SqlTableDescriptor WitnessArchive() => new(new("dbo", "OperationalMetricConflictWitnessArchive"),
        [C("WitnessId", "binary", 32), C("WitnessCodecVersion", "smallint"), C("WitnessPayload", "varbinary", -1),
            C("MetricAggregationProcessorRowId", "bigint"), C("MetricInputStreamRowId", "bigint"), D("SourceRevisionPosition"),
            C("ContributionFactRowId", "bigint"), D("ContributionPosition")],
        Primary("PK_OMCWA", "WitnessId"), [],
        [Fk("FK_OMCWA_Source", ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"], "MetricAggregationProcessor", ["MetricAggregationProcessorRowId", "MetricInputStreamRowId"]),
            Fk("FK_OMCWA_Revision", ["MetricAggregationProcessorRowId", "SourceRevisionPosition"], "MetricAggregationRevision", ["MetricAggregationProcessorRowId", "Position"]),
            Fk("FK_OMCWA_Contribution", ["MetricAggregationProcessorRowId", "ContributionFactRowId"], "MetricAggregationContribution", ["MetricAggregationProcessorRowId", "MetricInputFactRowId"]),
            Fk("FK_OMCWA_FactPosition", ["ContributionFactRowId", "MetricInputStreamRowId", "ContributionPosition"], "MetricInputFact", ["MetricInputFactRowId", "MetricInputStreamRowId", "Position"])],
        [Check("CK_OMCWA_Codec", "([WitnessCodecVersion]=(1))"), Size("CK_OMCWA_Length", "WitnessPayload", 1, 65536),
            Check("CK_OMCWA_Positions", $"([SourceRevisionPosition]>=(1) AND [SourceRevisionPosition]<=({UInt64Maximum}) AND ([ContributionPosition]>=(1) AND [ContributionPosition]<=[SourceRevisionPosition]))")], [],
        [ImmutableTrigger("TR_OMCWA_Immutable", "OperationalMetricConflictWitnessArchive")]);

    private static SqlTableDescriptor ResultArchive() => new(new("dbo", "OperationalMetricPublicationResultArchive"),
        [C("ResultId", "binary", 32), C("OperationalMetricProjectionProcessorRowId", "bigint"), C("PublicationFormatVersion", "smallint"),
            C("MetricAggregationProcessorRowId", "bigint"), C("MetricInputStreamRowId", "bigint"), D("SourceRevisionPosition"),
            C("EvaluationKeyHash", "binary", 32), C("EvaluationKeyBinary", "varbinary", -1), C("EvaluationPolicyId", "nvarchar", 64),
            C("ResultCodecVersion", "smallint"), C("ResultPayload", "varbinary", -1), C("Status", "tinyint"), C("ReasonCode", "tinyint", nullable: true), C("ConflictWitnessId", "binary", 32, true)],
        Primary("PK_OMPRA", "ResultId"),
        [Unique("UQ_OMPRA_Slot", "OperationalMetricProjectionProcessorRowId", "EvaluationKeyHash", "SourceRevisionPosition"),
            Unique("UQ_OMPRA_RetainedBinding", "ResultId", "OperationalMetricProjectionProcessorRowId", "EvaluationKeyHash", "SourceRevisionPosition")],
        [Fk("FK_OMPRA_ProcessorFormat", ["OperationalMetricProjectionProcessorRowId", "PublicationFormatVersion"], "OperationalMetricProjectionProcessor", ["OperationalMetricProjectionProcessorRowId", "PublicationFormatVersion"]),
            Fk("FK_OMPRA_ProcessorSource", ["OperationalMetricProjectionProcessorRowId", "MetricAggregationProcessorRowId", "MetricInputStreamRowId"], "OperationalMetricProjectionProcessor", ["OperationalMetricProjectionProcessorRowId", "MetricAggregationProcessorRowId", "MetricInputStreamRowId"]),
            Fk("FK_OMPRA_Revision", ["MetricAggregationProcessorRowId", "SourceRevisionPosition"], "MetricAggregationRevision", ["MetricAggregationProcessorRowId", "Position"]),
            Fk("FK_OMPRA_RootWitness", ["ConflictWitnessId"], "OperationalMetricConflictWitnessArchive", ["WitnessId"])],
        [Check("CK_OMPRA_Format", "([PublicationFormatVersion]=(2) AND [ResultCodecVersion]=(2))"), Check("CK_OMPRA_Policy", $"([EvaluationPolicyId]={Policy})"),
            Size("CK_OMPRA_KeyLength", "EvaluationKeyBinary", 1, 6992), Size("CK_OMPRA_PayloadLength", "ResultPayload", 1, 16777216),
            Check("CK_OMPRA_Position", $"([SourceRevisionPosition]>=(1) AND [SourceRevisionPosition]<=({UInt64Maximum}))"),
            Check("CK_OMPRA_StatusReason", "([Status]=(0) AND [ReasonCode] IS NULL OR [Status]=(1) AND [ReasonCode] IS NOT NULL AND ([ReasonCode]=(6) OR [ReasonCode]=(4) OR [ReasonCode]=(3) OR [ReasonCode]=(2)) OR [Status]=(2) AND [ReasonCode] IS NOT NULL AND ([ReasonCode]=(5) OR [ReasonCode]=(1) OR [ReasonCode]=(0)))"),
            Check("CK_OMPRA_WitnessShape", WitnessShape)], [], [ImmutableTrigger("TR_OMPRA_Immutable", "OperationalMetricPublicationResultArchive")]);

    private static SqlTableDescriptor WitnessReferences() => new(new("dbo", "OperationalMetricResultWitnessReference"),
        [C("ResultId", "binary", 32), C("NodePath", "varbinary", 256), C("WitnessId", "binary", 32)], Primary("PK_OMRWR", "ResultId", "NodePath"), [],
        [Fk("FK_OMRWR_Result", ["ResultId"], "OperationalMetricPublicationResultArchive", ["ResultId"]),
            Fk("FK_OMRWR_Witness", ["WitnessId"], "OperationalMetricConflictWitnessArchive", ["WitnessId"])],
        [Check("CK_OMRWR_PathLength", "(datalength([NodePath])>=(4) AND datalength([NodePath])<=(256) AND datalength([NodePath])%(4)=(0))")], [],
        [ImmutableTrigger("TR_OMRWR_Immutable", "OperationalMetricResultWitnessReference")]);

    private static SqlColumnDescriptor C(string name, string type, int? length = null, bool nullable = false) =>
        new(name, type, length is null ? null : length == -1 ? SqlLengthDescriptor.Max : SqlLengthDescriptor.Bounded(length.Value),
            null, null, nullable, type == "nvarchar" ? "Latin1_General_100_BIN2" : null, null);
    private static SqlColumnDescriptor D(string name) => new(name, "decimal", null, 20, 0, false, null, null);
    private static SqlIndexStructureDescriptor Index(bool clustered, string[] columns) => new(clustered,
        columns.Select((name, i) => new SqlIndexColumnDescriptor(name, SqlIndexColumnDirection.Ascending, i + 1)).ToImmutableArray(), [], null);
    private static SqlPrimaryKeyDescriptor Primary(string name, params string[] columns) => new(name, Index(true, columns));
    private static SqlUniqueConstraintDescriptor Unique(string name, params string[] columns) => new(name, Index(false, columns));
    private static SqlForeignKeyDescriptor Fk(string name, ImmutableArray<string> columns, string table, ImmutableArray<string> target) =>
        new(name, columns, new("dbo", table), target, SqlReferentialAction.NoAction, SqlReferentialAction.NoAction, true, true, false);
    private static SqlCheckConstraintDescriptor Check(string name, string definition) => new(name, definition, true, true, false);
    private static SqlCheckConstraintDescriptor Size(string name, string column, int min, int max) => Check(name, $"(datalength([{column}])>=({min}) AND datalength([{column}])<=({max}))");
    private static SqlTriggerDescriptor ImmutableTrigger(string name, string table) => new(name, $"""
        CREATE TRIGGER dbo.{name}
        ON dbo.{table}
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM deleted)
                THROW 51021, 'Immutable operational metric archive cannot be updated or deleted.', 1;
        END;
        """, true, false, false);
}
