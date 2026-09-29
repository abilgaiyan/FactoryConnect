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
                    UInt64("StartingProductionReferenceTimeRevision"),
                    Column("State", "tinyint"),
                    UInt64("FinalProductionReferenceTimeRevision", isNullable: true)
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
                        "FK_ProductionReferenceTimePublicationTransition_StartingReferenceRevision",
                        ["MetricAggregationProcessorRowId", "StartingProductionReferenceTimeRevision"],
                        "ProductionReferenceTimeRevision",
                        ["MetricAggregationProcessorRowId", "ProductionReferenceTimeRevision"]),
                    ForeignKey(
                        "FK_ProductionReferenceTimePublicationTransition_FinalReferenceRevision",
                        ["MetricAggregationProcessorRowId", "FinalProductionReferenceTimeRevision"],
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
                        "CK_ProductionReferenceTimePublicationTransition_StartingReferenceRevision",
                        "([StartingProductionReferenceTimeRevision]>=(0) AND [StartingProductionReferenceTimeRevision]<=(18446744073709551615.))"),
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_FinalReferenceRevision",
                        "([FinalProductionReferenceTimeRevision] IS NULL OR [FinalProductionReferenceTimeRevision]>=(0) AND [FinalProductionReferenceTimeRevision]<=(18446744073709551615.))"),
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_State",
                        "([State]=(0) AND [FinalProductionReferenceTimeRevision] IS NULL OR [State]=(1) AND [FinalProductionReferenceTimeRevision] IS NOT NULL)"),
                    Check(
                        "CK_ProductionReferenceTimePublicationTransition_CompletionRevision",
                        "([State]=(0) OR [ProductionStandardAuthorityRevision] IS NULL AND [FinalProductionReferenceTimeRevision]=[StartingProductionReferenceTimeRevision] OR [ProductionStandardAuthorityRevision] IS NOT NULL AND [FinalProductionReferenceTimeRevision]>[StartingProductionReferenceTimeRevision])")
                ],
                indexes:
                [
                    UniqueFilteredIndex(
                        "UX_ProductionReferenceTimePublicationTransition_Pending",
                        ["MetricAggregationProcessorRowId"],
                        "([State]=(0))"),
                    UniqueFilteredIndex(
                        "UX_ProductionReferenceTimePublicationTransition_Successor",
                        ["MetricAggregationProcessorRowId", "ExpectedPreviousMetricAggregationPosition"],
                        "([ExpectedPreviousMetricAggregationPosition] IS NOT NULL)"),
                    UniqueFilteredIndex(
                        "UX_ProductionReferenceTimePublicationTransition_BootstrapSuccessor",
                        ["MetricAggregationProcessorRowId"],
                        "([ExpectedPreviousMetricAggregationPosition] IS NULL)")
                ])
        ]);
    }

    private static SqlTableDescriptor Table(
        string name,
        ImmutableArray<SqlColumnDescriptor> columns,
        SqlPrimaryKeyDescriptor primaryKey,
        ImmutableArray<SqlForeignKeyDescriptor> foreignKeys = default,
        ImmutableArray<SqlCheckConstraintDescriptor> checks = default,
        ImmutableArray<SqlIndexDescriptor> indexes = default) =>
        new(
            new SqlObjectName("dbo", name),
            columns,
            primaryKey,
            [],
            foreignKeys.IsDefault ? [] : foreignKeys,
            checks.IsDefault ? [] : checks,
            indexes.IsDefault ? [] : indexes);

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
            IndexColumns(columns),
            [],
            null));

    private static SqlIndexDescriptor UniqueFilteredIndex(
        string name,
        string[] columns,
        string filter) =>
        new(
            name,
            IsUnique: true,
            IsEnabled: true,
            new SqlIndexStructureDescriptor(
                IsClustered: false,
                KeyColumns: IndexColumns(columns),
                IncludedColumns: [],
                CanonicalFilterDefinition: filter));

    private static ImmutableArray<SqlIndexColumnDescriptor> IndexColumns(IEnumerable<string> columns) =>
        columns
            .Select(static (column, index) =>
                new SqlIndexColumnDescriptor(column, SqlIndexColumnDirection.Ascending, index + 1))
            .ToImmutableArray();

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
