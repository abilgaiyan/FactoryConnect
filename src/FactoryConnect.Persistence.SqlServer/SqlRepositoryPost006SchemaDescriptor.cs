using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost006SchemaDescriptor
{
    private static readonly SqlObjectName ManifestTableName =
        new("dbo", "OperationalMetricProjectionManifest");

    public static SqlSchemaDescriptor Create(SqlSchemaDescriptor post005)
    {
        ArgumentNullException.ThrowIfNull(post005);

        var tables = post005.Tables
            .Select(static table =>
                table.Name == ManifestTableName
                    ? ReplaceManifestParentForeignKey(table)
                    : table)
            .ToImmutableArray();

        return new SqlSchemaDescriptor(tables);
    }

    private static SqlTableDescriptor ReplaceManifestParentForeignKey(
        SqlTableDescriptor manifest)
    {
        var checkpointForeignKey = manifest.ForeignKeys.SingleOrDefault(
            static foreignKey => string.Equals(
                foreignKey.Name,
                "FK_OperationalMetricProjectionManifest_Checkpoint",
                StringComparison.Ordinal));

        if (checkpointForeignKey is null)
        {
            throw new InvalidOperationException(
                "Post-005 manifest descriptor does not contain the historical checkpoint foreign key.");
        }

        var remainingForeignKeys = manifest.ForeignKeys
            .Where(static foreignKey => !string.Equals(
                foreignKey.Name,
                "FK_OperationalMetricProjectionManifest_Checkpoint",
                StringComparison.Ordinal));

        var processorForeignKey = new SqlForeignKeyDescriptor(
            "FK_OperationalMetricProjectionManifest_Processor",
            ["OperationalMetricProjectionProcessorRowId"],
            new SqlObjectName("dbo", "OperationalMetricProjectionProcessor"),
            ["OperationalMetricProjectionProcessorRowId"],
            SqlReferentialAction.NoAction,
            SqlReferentialAction.NoAction,
            IsEnabled: true,
            IsTrusted: true,
            IsNotForReplication: false);

        return manifest with
        {
            ForeignKeys = [.. remainingForeignKeys, processorForeignKey]
        };
    }
}
