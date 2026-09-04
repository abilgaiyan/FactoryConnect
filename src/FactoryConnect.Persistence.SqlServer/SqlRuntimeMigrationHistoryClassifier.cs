using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal enum SqlRuntimeMigrationHistoryClassification
{
    ExactCurrent,
    ExactPrefixPending,
    DatabaseNewerThanSupported,
    IdentityMismatch,
    ChecksumMismatch,
    RowSemanticsInvalid
}

internal static class SqlRuntimeMigrationHistoryClassifier
{
    public static SqlRuntimeMigrationHistoryClassification Classify(
        ImmutableArray<SqlMigrationHistoryRow> history,
        SqlMigrationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var row in history)
        {
            if (!SqlMigrationHistoryRowValidator.IsCanonicalChecksum(row.CanonicalChecksum) ||
                row.AppliedAtUtc.Offset != TimeSpan.Zero)
            {
                return SqlRuntimeMigrationHistoryClassification.RowSemanticsInvalid;
            }
        }

        var overlappingCount = Math.Min(history.Length, catalog.Migrations.Length);
        for (var index = 0; index < overlappingCount; index++)
        {
            var row = history[index];
            var descriptor = catalog.Migrations[index];

            if (row.MigrationId != descriptor.MigrationId ||
                !string.Equals(row.Name, descriptor.Name, StringComparison.Ordinal))
            {
                return SqlRuntimeMigrationHistoryClassification.IdentityMismatch;
            }

            if (!string.Equals(row.CanonicalChecksum, descriptor.Sha256Checksum, StringComparison.Ordinal))
            {
                return SqlRuntimeMigrationHistoryClassification.ChecksumMismatch;
            }
        }

        if (history.Length < catalog.Migrations.Length)
        {
            return SqlRuntimeMigrationHistoryClassification.ExactPrefixPending;
        }

        if (history.Length == catalog.Migrations.Length)
        {
            return SqlRuntimeMigrationHistoryClassification.ExactCurrent;
        }

        var previousMigrationId = catalog.Migrations[^1].MigrationId;
        for (var index = catalog.Migrations.Length; index < history.Length; index++)
        {
            var row = history[index];
            if (row.MigrationId <= previousMigrationId || string.IsNullOrWhiteSpace(row.Name))
            {
                return SqlRuntimeMigrationHistoryClassification.IdentityMismatch;
            }

            previousMigrationId = row.MigrationId;
        }

        return SqlRuntimeMigrationHistoryClassification.DatabaseNewerThanSupported;
    }
}
