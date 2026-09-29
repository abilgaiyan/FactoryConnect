using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost014SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post013)
    {
        ArgumentNullException.ThrowIfNull(post013);

        return new SqlSchemaDescriptor(
        [
            .. post013.Tables,
            Table(
                "ProductionReferenceTimePublicationTransition",
                [
                    Column("MetricAggregationProcessorRowId", "bigint"),
                    UInt64("TargetMetricAggregationPosition"),
                    UInt64("ExpectedPreviousMetricAggregationPosition", isNullable: true),
                    UInt64("ProductionStandardAuthorityRevision", isNullable: true),
                    UInt64("CompletedProductionReferenceTimeRevision", isNullable: true),
                    Column("IsCompleted", "bit")
                ],
                PrimaryKey(
                    "PK_ProductionReferenceTimePublicationTransition",
                    "MetricAggregationProcessorRowId",
                    "TargetMetricAggregationPosition"),
                foreignKeys:
                [
                    ForeignKey(
                        "FK_ProductionReferenceTimePublicationTransition_TargetAggregation",
                        ["MetricAggregationProcessorRowId", "TargetMetricAggregationPosition"],
                        "MetricAggregationRevision",
                        ["MetricAggregationProcessorRowId", "Position"]),
                    ForeignKey(
                        "FK_ProductionReferenceTimePublicationTransition_PreviousAggregation",
                        ["MetricAggregationProcessorRowId", "ExpectedPreviousMetricAggregationPosition"],
                        "MetricAggregationRevision",
                        ["MetricAggregationProcessorRowId", "Position"]),
                    ForeignKey(
                        "FK_ProductionReferenceTimePublicationTransition_StandardRevision",
                        ["ProductionStandardAuthorityRevision"],
                        "ProductionStandardAuthorityRevision",
                        ["ProductionStandardAuthorityRevision"]),
                    ForeignKey(
                        "FK_ProductionReferenceTimePublicationTransition_CompletedReferenceRevision",
                        ["MetricAggregationProcessorRowId", "CompletedProductionReferenceTimeRevision"],
                        "ProductionReferenceTimeRevision",
                        ["MetricAggregationProcessorRowId", "ProductionReferenceTimeRevision"])
                ],
                checks:
                [
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_TargetPosition",
                        "([TargetMetricAggregationPosition]>=(1) AND [TargetMetricAggregationPosition]<=(18446744073709551615.))"),
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_PreviousPosition",
                        "([ExpectedPreviousMetricAggregationPosition] IS NULL OR [ExpectedPreviousMetricAggregationPosition]>=(1) AND [ExpectedPreviousMetricAggregationPosition]<[TargetMetricAggregationPosition] AND [ExpectedPreviousMetricAggregationPosition]<=(18446744073709551615.))"),
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_StandardRevision",
                        "([ProductionStandardAuthorityRevision] IS NULL OR [ProductionStandardAuthorityRevision]>=(0) AND [ProductionStandardAuthorityRevision]<=(18446744073709551615.))"),
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_CompletedReferenceRevision",
                        "([CompletedProductionReferenceTimeRevision] IS NULL OR [CompletedProductionReferenceTimeRevision]>=(0) AND [CompletedProductionReferenceTimeRevision]<=(18446744073709551615.))"),
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_State",
                        "([IsCompleted]=(0) AND [CompletedProductionReferenceTimeRevision] IS NULL OR [IsCompleted]=(1) AND [CompletedProductionReferenceTimeRevision] IS NOT NULL)")
                ])
        ]);
    }

    private static SqlTableDescriptor Table(
        string name,
        ImmutableArray<SqlColumnDescriptor> columns,
        SqlPrimaryKeyDescriptor primaryKey,
        ImmutableArray<SqlForeignKeyDescriptor> foreignKeys = default,
        ImmutableArray<SqlCheckConstraintDescriptor> checks = default) =>
        new(
            new SqlObjectName("dbo", name),
            columns,
            primaryKey,
            [],
            foreignKeys.IsDefault ? [] : foreignKeys,
            checks.IsDefault ? [] : checks,
            []);

    private static SqlColumnDescriptor Column(
        string name,
        string sqlType,
        bool isNullable = false) =>
        new(name, sqlType, null, null, null, isNullable, null, null);

    private static SqlColumnDescriptor UInt64(string name, bool isNullable = false) =>
        new(name, "decimal", null, 20, 0, isNullable, null, null);

    private static SqlPrimaryKeyDescriptor PrimaryKey(string name, params string[] columns) =>
        new(name, new SqlIndexStructureDescriptor(
            true,
            columns.Select(static (column, index) =>
                new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1)).ToImmutableArray(),
            [],
            null));

    private static SqlForeignKeyDescriptor ForeignKey(
        string name,
        string[] columns,
        string referencedTable,
        string[] referencedColumns) =>
        new(
            name,
            columns.ToImmutableArray(),
            new SqlObjectName("dbo", referencedTable),
            referencedColumns.ToImmutableArray(),
            SqlReferentialAction.NoAction,
            SqlReferentialAction.NoAction,
            true,
            true,
            false);

    private static SqlCheckConstraintDescriptor Check(string name, string definition) =>
        new(name, definition, true, true, false);
}
