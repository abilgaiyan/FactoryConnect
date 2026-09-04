using System.Collections.Immutable;
using System.Globalization;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

internal sealed record SqlRuntimeCompatibilityPersistentStateSnapshot(
    SqlMigrationLedgerObjectKind LedgerObjectKind,
    int? LedgerObjectId,
    string? LedgerCatalogObjectType,
    ImmutableArray<string> LedgerStructure,
    ImmutableArray<string> LedgerRows,
    SqlSchemaDescriptor OwnedSchema,
    string UnrelatedObjectType,
    int UnrelatedMarker)
{
    public static async Task<SqlRuntimeCompatibilityPersistentStateSnapshot> CaptureAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            cancellationToken);
        try
        {
            var ledgerReader = new SqlServerMigrationLedgerMetadataReader();
            var ledgerState = await ledgerReader.ResolveObjectAsync(
                connection,
                transaction,
                cancellationToken);

            var ledgerStructure = ImmutableArray<string>.Empty;
            var ledgerRows = ImmutableArray<string>.Empty;
            if (ledgerState.Kind == SqlMigrationLedgerObjectKind.UserTable)
            {
                var objectId = ledgerState.ObjectId ?? throw new InvalidOperationException(
                    "Resolved migration ledger user table has no object id.");
                var schema = await ledgerReader.ReadSchemaAsync(
                    connection,
                    transaction,
                    objectId,
                    cancellationToken);
                ledgerStructure = ProjectLedgerStructure(schema);

                var history = await new SqlServerRuntimeMigrationHistoryReader().ReadAsync(
                    connection,
                    transaction,
                    cancellationToken);
                ledgerRows = history
                    .OrderBy(static row => row.MigrationId)
                    .Select(static row => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{row.MigrationId}|{row.Name}|{row.CanonicalChecksum}|{row.AppliedAtUtc:O}"))
                    .ToImmutableArray();
            }

            var ownedSchema = await new SqlServerSchemaMetadataReader()
                .ReadFactoryConnectOwnedSchemaInTransactionAsync(
                    connection,
                    transaction,
                    cancellationToken);
            var (unrelatedObjectType, unrelatedMarker) = await ReadUnrelatedSentinelAsync(
                connection,
                transaction,
                cancellationToken);

            return new SqlRuntimeCompatibilityPersistentStateSnapshot(
                ledgerState.Kind,
                ledgerState.ObjectId,
                ledgerState.CatalogObjectType,
                ledgerStructure,
                ledgerRows,
                ownedSchema,
                unrelatedObjectType,
                unrelatedMarker);
        }
        finally
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    public static void AssertEquivalent(
        SqlRuntimeCompatibilityPersistentStateSnapshot expected,
        SqlRuntimeCompatibilityPersistentStateSnapshot actual)
    {
        Assert.Equal(expected.LedgerObjectKind, actual.LedgerObjectKind);
        Assert.Equal(expected.LedgerObjectId, actual.LedgerObjectId);
        Assert.Equal(expected.LedgerCatalogObjectType, actual.LedgerCatalogObjectType);
        Assert.Equal<string>(expected.LedgerStructure, actual.LedgerStructure);
        Assert.Equal<string>(expected.LedgerRows, actual.LedgerRows);
        Assert.Equal(expected.UnrelatedObjectType, actual.UnrelatedObjectType);
        Assert.Equal(expected.UnrelatedMarker, actual.UnrelatedMarker);

        var schemaComparison = SqlSchemaComparator.Compare(expected.OwnedSchema, actual.OwnedSchema);
        Assert.True(
            schemaComparison.IsExactMatch,
            "FactoryConnect-owned persistent schema changed during runtime verification: " +
            string.Join(Environment.NewLine, schemaComparison.Differences));
    }

    private static ImmutableArray<string> ProjectLedgerStructure(
        SqlMigrationLedgerSchemaSnapshot schema)
    {
        var lines = ImmutableArray.CreateBuilder<string>();
        foreach (var column in schema.Columns.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            var length = column.MaxLength is null
                ? "<none>"
                : column.MaxLength.Value.IsMax
                    ? "max"
                    : column.MaxLength.Value.Value!.Value.ToString(CultureInfo.InvariantCulture);
            var identity = column.Identity is null
                ? "<none>"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{column.Identity.SeedValue}|{column.Identity.IncrementValue}|{column.Identity.IsNotForReplication}");
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"COLUMN|{column.Name}|{column.SqlType}|{length}|{column.Scale}|{column.IsNullable}|{column.Collation ?? "<none>"}|{identity}"));
        }

        if (schema.PrimaryKey is SqlMigrationLedgerPrimaryKeyDescriptor primaryKey)
        {
            var keys = string.Join(
                ",",
                primaryKey.KeyColumns
                    .OrderBy(static item => item.Ordinal)
                    .Select(static item => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{item.Ordinal}:{item.Name}:{item.Direction}")));
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"PRIMARY_KEY|{primaryKey.Name}|{primaryKey.IsClustered}|{primaryKey.IsEnabled}|{keys}"));
        }
        else
        {
            lines.Add("PRIMARY_KEY|<none>");
        }

        AddNamedArtifacts(lines, "UNIQUE", schema.UniqueConstraints);
        AddNamedArtifacts(lines, "FOREIGN_KEY", schema.ForeignKeys);
        AddNamedArtifacts(lines, "DEFAULT", schema.DefaultConstraints);
        AddNamedArtifacts(lines, "CHECK", schema.CheckConstraints);
        AddNamedArtifacts(lines, "INDEX", schema.OrdinaryIndexes);
        AddNamedArtifacts(lines, "TRIGGER", schema.Triggers);
        return lines.ToImmutable();
    }

    private static void AddNamedArtifacts(
        ImmutableArray<string>.Builder lines,
        string kind,
        ImmutableArray<string> names)
    {
        foreach (var name in names.OrderBy(static item => item, StringComparer.Ordinal))
        {
            lines.Add($"{kind}|{name}");
        }
    }

    private static async Task<(string ObjectType, int Marker)> ReadUnrelatedSentinelAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.type, s.Marker
            FROM sys.objects AS o
            CROSS JOIN dbo.D5UnrelatedSentinel AS s
            WHERE o.object_id = OBJECT_ID(N'dbo.D5UnrelatedSentinel')
              AND s.Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("D.5 unrelated sentinel is missing.");
        }

        var result = (reader.GetString(0), reader.GetInt32(1));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("D.5 unrelated sentinel returned multiple rows.");
        }

        return result;
    }
}
