using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlMigrationLedgerObjectKind
{
    Absent,
    UserTable,
    IncompatibleObject
}

internal sealed record SqlMigrationLedgerObjectState(
    SqlMigrationLedgerObjectKind Kind,
    int? ObjectId,
    string? CatalogObjectType);

internal sealed record SqlMigrationLedgerColumnDescriptor(
    string Name,
    string SqlType,
    SqlLengthDescriptor? MaxLength,
    byte? Scale,
    bool IsNullable,
    string? Collation,
    SqlIdentityDescriptor? Identity);

internal sealed record SqlMigrationLedgerPrimaryKeyDescriptor(
    string Name,
    bool IsClustered,
    bool IsEnabled,
    ImmutableArray<SqlIndexColumnDescriptor> KeyColumns);

internal sealed record SqlMigrationLedgerSchemaSnapshot(
    ImmutableArray<SqlMigrationLedgerColumnDescriptor> Columns,
    SqlMigrationLedgerPrimaryKeyDescriptor? PrimaryKey,
    ImmutableArray<string> UniqueConstraints,
    ImmutableArray<string> ForeignKeys,
    ImmutableArray<string> DefaultConstraints,
    ImmutableArray<string> CheckConstraints,
    ImmutableArray<string> OrdinaryIndexes,
    ImmutableArray<string> Triggers);

internal sealed record SqlMigrationHistoryRow(
    int MigrationId,
    string Name,
    string CanonicalChecksum,
    DateTimeOffset AppliedAtUtc);

internal sealed class SqlMigrationLedgerSchemaException : InvalidOperationException
{
    public SqlMigrationLedgerSchemaException(string message)
        : base(message)
    {
    }
}

internal sealed class SqlMigrationHistoryException : InvalidOperationException
{
    public SqlMigrationHistoryException(string message)
        : base(message)
    {
    }
}

internal static class SqlMigrationLedgerContract
{
    public const string SchemaName = "dbo";
    public const string TableName = "FactoryConnectMigrationHistory";
    public const string PrimaryKeyName = "PK_FactoryConnectMigrationHistory";
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    public static ImmutableArray<SqlMigrationLedgerColumnDescriptor> Columns { get; } =
    [
        new("MigrationId", "int", null, null, IsNullable: false, Collation: null, Identity: null),
        new("Name", "nvarchar", SqlLengthDescriptor.Bounded(128), null, IsNullable: false, BinaryCollation, Identity: null),
        new("CanonicalChecksum", "char", SqlLengthDescriptor.Bounded(64), null, IsNullable: false, BinaryCollation, Identity: null),
        new("AppliedAtUtc", "datetimeoffset", null, 7, IsNullable: false, Collation: null, Identity: null)
    ];

    public static SqlMigrationLedgerPrimaryKeyDescriptor PrimaryKey { get; } = new(
        PrimaryKeyName,
        IsClustered: true,
        IsEnabled: true,
        [new SqlIndexColumnDescriptor("MigrationId", SqlIndexColumnDirection.Ascending, 1)]);
}

internal static class SqlMigrationHistoryRowValidator
{
    public static void Validate(SqlMigrationHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!IsCanonicalChecksum(row.CanonicalChecksum))
        {
            throw new SqlMigrationHistoryException(
                $"Migration history row '{row.MigrationId:000}' has an invalid canonical checksum.");
        }

        if (row.AppliedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new SqlMigrationHistoryException(
                $"Migration history row '{row.MigrationId:000}' must use UTC offset zero.");
        }
    }

    internal static bool IsCanonicalChecksum(string checksum)
    {
        if (checksum is null || checksum.Length != 64)
        {
            return false;
        }

        foreach (var character in checksum)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}

internal static class SqlMigrationHistoryPrefixValidator
{
    public static int ValidateExactPrefix(
        ImmutableArray<SqlMigrationHistoryRow> history,
        SqlMigrationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (history.Length > catalog.Migrations.Length)
        {
            throw new SqlMigrationHistoryException(
                "Migration history contains more rows than the repository catalog and is not an exact prefix.");
        }

        for (var index = 0; index < history.Length; index++)
        {
            var row = history[index];
            SqlMigrationHistoryRowValidator.Validate(row);
            var descriptor = catalog.Migrations[index];

            if (row.MigrationId != descriptor.MigrationId ||
                !string.Equals(row.Name, descriptor.Name, StringComparison.Ordinal) ||
                !string.Equals(row.CanonicalChecksum, descriptor.Sha256Checksum, StringComparison.Ordinal))
            {
                throw new SqlMigrationHistoryException(
                    $"Migration history row '{row.MigrationId:000}' does not match repository catalog position {index + 1}.");
            }
        }

        return history.Length;
    }
}
