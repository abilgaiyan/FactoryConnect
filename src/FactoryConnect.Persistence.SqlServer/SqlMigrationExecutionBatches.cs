namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Explicit compilation boundaries for reviewed migration 021 only.</summary>
internal static class SqlMigrationExecutionBatches
{
    internal const string Marker = "-- FACTORYCONNECT MIGRATION BATCH --";
    internal const string ContainmentMigrationName = "DurableOperationalMetricContainment";

    public static string[] For(SqlMigrationDescriptor migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        if (migration.MigrationId != 21)
        {
            if (migration.CanonicalSql.Contains(Marker, StringComparison.Ordinal))
                throw new InvalidDataException("Execution delimiters require an explicitly reviewed migration plan.");
            return [migration.CanonicalSql];
        }
        if (migration.Name != ContainmentMigrationName || migration.TransactionPolicy != SqlMigrationTransactionPolicy.EngineOwned ||
            migration.ResourceName != SqlMigrationCatalog.ResourcePrefix + "021_" + ContainmentMigrationName + ".sql")
            throw new InvalidDataException("Migration 021 does not match its reviewed execution identity.");

        var batches = migration.CanonicalSql.ReplaceLineEndings("\n").Split("\n" + Marker + "\n", StringSplitOptions.None);
        if (batches.Length != 12 || batches.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Migration 021 requires exactly twelve nonempty compilation batches.");
        // Validate the complete resource and each command. No GO, transaction
        // control, connection change or per-command commit is introduced.
        SqlMigrationLexicalPolicy.Validate(migration.CanonicalSql, migration.TransactionPolicy);
        foreach (var batch in batches) SqlMigrationLexicalPolicy.Validate(batch, migration.TransactionPolicy);
        if (batches.Skip(9).Any(static batch => !batch.TrimStart().StartsWith("CREATE TRIGGER", StringComparison.Ordinal)))
            throw new InvalidDataException("Migration 021 trigger definitions must start their own batches.");
        return batches;
    }
}
