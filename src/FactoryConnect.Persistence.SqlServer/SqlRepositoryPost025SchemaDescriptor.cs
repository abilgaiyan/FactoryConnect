using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost025SchemaDescriptor
{
    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor previous) => new(
        previous.Tables.Select(table =>
        {
            if (table.Name.ObjectName != "SupervisorProductionEntry")
                return table;

            return table with
            {
                Columns =
                [
                    .. table.Columns,
                    new SqlColumnDescriptor("GoodQuantity", "bigint", null, null, null, true, null, null),
                    new SqlColumnDescriptor("RejectedQuantity", "bigint", null, null, null, true, null, null)
                ],
                CheckConstraints =
                [
                    .. table.CheckConstraints,
                    new SqlCheckConstraintDescriptor(
                        "CK_SupervisorProductionEntry_QuantityPair",
                        "(([GoodQuantity] IS NULL AND [RejectedQuantity] IS NULL) OR ([GoodQuantity] IS NOT NULL AND [RejectedQuantity] IS NOT NULL AND [GoodQuantity]>=(0) AND [RejectedQuantity]>=(0)))",
                        true, true, false)
                ]
            };
        }).ToImmutableArray());
}
