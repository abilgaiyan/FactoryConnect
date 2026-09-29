using System.Collections.Immutable;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerRuntimeSchemaCompatibilityVerifier
{
    private readonly SqlMigrationCatalog _catalog;
    private readonly SqlServerMigrationLedgerMetadataReader _ledgerMetadataReader;
    private readonly SqlServerRuntimeMigrationHistoryReader _historyReader;
    private readonly SqlServerSchemaMetadataReader _schemaMetadataReader;

    public SqlServerRuntimeSchemaCompatibilityVerifier(SqlMigrationCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ledgerMetadataReader = new SqlServerMigrationLedgerMetadataReader();
        _historyReader = new SqlServerRuntimeMigrationHistoryReader();
        _schemaMetadataReader = new SqlServerSchemaMetadataReader();
    }

    public static SqlServerRuntimeSchemaCompatibilityVerifier CreateDefault() =>
        new(SqlMigrationCatalog.Load());

    public async Task<SqlRuntimeCompatibilityResult> VerifyAsync(
        SqlConnection connection,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var scope = await SqlServerMigrationTransactionScope.BeginSharedAsync(
            connection,
            lockTimeout,
            cancellationToken);

        var result = await ClassifyAsync(
            connection,
            scope.Transaction,
            cancellationToken);

        await scope.RollbackAsync(cancellationToken);
        return result;
    }

    private async Task<SqlRuntimeCompatibilityResult> ClassifyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ledgerState = await _ledgerMetadataReader.ResolveObjectAsync(
            connection,
            transaction,
            cancellationToken);

        switch (ledgerState.Kind)
        {
            case SqlMigrationLedgerObjectKind.Absent:
                return await ClassifyWithoutLedgerAsync(connection, transaction, cancellationToken);

            case SqlMigrationLedgerObjectKind.IncompatibleObject:
                return new SqlRuntimeCompatibilityResult(
                    SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid,
                    SqlRuntimeCompatibilityDiagnostics.IncompatibleLedgerObject(ledgerState.CatalogObjectType));

            case SqlMigrationLedgerObjectKind.UserTable:
                return await ClassifyWithLedgerAsync(
                    connection,
                    transaction,
                    RequireObjectId(ledgerState),
                    cancellationToken);

            default:
                throw new InvalidOperationException(
                    $"Unsupported migration ledger object kind '{ledgerState.Kind}'.");
        }
    }

    private async Task<SqlRuntimeCompatibilityResult> ClassifyWithoutLedgerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ownedSchema = await _schemaMetadataReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            cancellationToken);

        return SqlUnledgeredDatabaseClassifier.Classify(ownedSchema) switch
        {
            UnledgeredDatabaseClassification.Uninitialized =>
                new SqlRuntimeCompatibilityResult(
                    SqlRuntimeCompatibilityClassification.DatabaseUninitialized,
                    SqlRuntimeCompatibilityDiagnostics.DatabaseUninitialized()),
            UnledgeredDatabaseClassification.LegacyAdoptable =>
                new SqlRuntimeCompatibilityResult(
                    SqlRuntimeCompatibilityClassification.LegacyAdoptionRequired,
                    SqlRuntimeCompatibilityDiagnostics.LegacyAdoptionRequired()),
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy =>
                CreateUnledgeredSchemaIncompatibleResult(ownedSchema),
            _ => throw new InvalidOperationException("Unsupported unledgered database classification."),
        };
    }

    private async Task<SqlRuntimeCompatibilityResult> ClassifyWithLedgerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int objectId,
        CancellationToken cancellationToken)
    {
        var ledgerSchema = await _ledgerMetadataReader.ReadSchemaAsync(
            connection,
            transaction,
            objectId,
            cancellationToken);

        try
        {
            SqlMigrationLedgerSchemaValidator.Validate(ledgerSchema);
        }
        catch (SqlMigrationLedgerSchemaException exception)
        {
            return new SqlRuntimeCompatibilityResult(
                SqlRuntimeCompatibilityClassification.MigrationLedgerSchemaInvalid,
                SqlRuntimeCompatibilityDiagnostics.InvalidLedgerStructure(exception));
        }

        var history = await _historyReader.ReadAsync(
            connection,
            transaction,
            cancellationToken);
        var historyClassification = SqlRuntimeMigrationHistoryClassifier.Classify(history, _catalog);
        var terminalClassification = SqlRuntimeMigrationHistoryCompatibilityMapping.MapTerminal(
            historyClassification);
        if (terminalClassification is SqlRuntimeCompatibilityClassification terminal)
        {
            return new SqlRuntimeCompatibilityResult(
                terminal,
                SqlRuntimeCompatibilityDiagnostics.ForHistory(historyClassification, history, _catalog));
        }

        var liveSchema = await _schemaMetadataReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            cancellationToken);
        var comparison = SqlSchemaComparator.Compare(
            SqlRepositorySchemaDescriptors.Current,
            liveSchema);
        return comparison.IsExactMatch
            ? new SqlRuntimeCompatibilityResult(
                SqlRuntimeCompatibilityClassification.Compatible,
                ImmutableArray<SqlRuntimeCompatibilityDiagnostic>.Empty)
            : new SqlRuntimeCompatibilityResult(
                SqlRuntimeCompatibilityClassification.MigrationSchemaDrift,
                SqlRuntimeCompatibilityDiagnostics.SchemaDrift(comparison));
    }

    private static SqlRuntimeCompatibilityResult CreateUnledgeredSchemaIncompatibleResult(
        SqlSchemaDescriptor ownedSchema)
    {
        var comparison = SqlSchemaComparator.Compare(
            SqlRepositorySchemaDescriptors.LegacyPost004,
            ownedSchema);
        return new SqlRuntimeCompatibilityResult(
            SqlRuntimeCompatibilityClassification.UnledgeredSchemaIncompatible,
            SqlRuntimeCompatibilityDiagnostics.UnledgeredSchemaIncompatible(comparison));
    }

    private static int RequireObjectId(SqlMigrationLedgerObjectState state) =>
        state.ObjectId ?? throw new SqlMigrationLedgerSchemaException(
            "FactoryConnect migration ledger object id is missing for a resolved user table.");
}
