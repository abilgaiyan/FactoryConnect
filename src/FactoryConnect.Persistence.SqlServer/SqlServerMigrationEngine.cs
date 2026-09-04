using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class UnledgeredSchemaIncompatibleException : InvalidOperationException
{
    public UnledgeredSchemaIncompatibleException()
        : base("FactoryConnect-owned schema is present without a migration ledger but does not exactly match the legacy post-004 adoption descriptor.")
    {
    }
}

internal sealed class FinalSchemaValidationException : InvalidOperationException
{
    public FinalSchemaValidationException(string message)
        : base(message)
    {
    }
}

internal sealed class SqlServerMigrationEngine
{
    private readonly SqlMigrationCatalog _catalog;
    private readonly SqlServerMigrationLedgerMetadataReader _ledgerMetadataReader;
    private readonly SqlServerSchemaMetadataReader _schemaMetadataReader;
    private readonly SqlServerMigrationHistoryStore _historyStore;

    public SqlServerMigrationEngine(
        SqlMigrationCatalog catalog,
        ISqlMigrationUtcClock clock)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(clock);

        _ledgerMetadataReader = new SqlServerMigrationLedgerMetadataReader();
        _schemaMetadataReader = new SqlServerSchemaMetadataReader();
        _historyStore = new SqlServerMigrationHistoryStore(clock);
    }

    public static SqlServerMigrationEngine CreateDefault() =>
        new(SqlMigrationCatalog.Load(), new SystemSqlMigrationUtcClock());

    public async Task ApplyAsync(
        SqlConnection connection,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var scope = await SqlServerMigrationTransactionScope.BeginAsync(
            connection,
            lockTimeout,
            cancellationToken);

        var transaction = scope.Transaction;
        var ledgerState = await _ledgerMetadataReader.ResolveObjectAsync(
            connection,
            transaction,
            cancellationToken);

        switch (ledgerState.Kind)
        {
            case SqlMigrationLedgerObjectKind.Absent:
                await ApplyWithoutLedgerAsync(connection, transaction, cancellationToken);
                break;

            case SqlMigrationLedgerObjectKind.UserTable:
                await ApplyWithLedgerAsync(
                    connection,
                    transaction,
                    RequireObjectId(ledgerState),
                    cancellationToken);
                break;

            case SqlMigrationLedgerObjectKind.IncompatibleObject:
                throw new SqlMigrationLedgerSchemaException(
                    $"FactoryConnect migration ledger identity is occupied by incompatible SQL object type '{ledgerState.CatalogObjectType ?? "<unknown>"}'.");

            default:
                throw new InvalidOperationException(
                    $"Unsupported migration ledger object kind '{ledgerState.Kind}'.");
        }

        await ValidateFinalStateAsync(connection, transaction, cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    private async Task ApplyWithoutLedgerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ownedSchema = await _schemaMetadataReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            cancellationToken);

        switch (SqlUnledgeredDatabaseClassifier.Classify(ownedSchema))
        {
            case UnledgeredDatabaseClassification.Uninitialized:
                await SqlServerMigrationLedgerCreator.CreateAsync(
                    connection,
                    transaction,
                    cancellationToken);
                await ExecuteAndRecordAsync(
                    connection,
                    transaction,
                    startIndex: 0,
                    cancellationToken);
                break;

            case UnledgeredDatabaseClassification.LegacyAdoptable:
                await SqlServerMigrationLedgerCreator.CreateAsync(
                    connection,
                    transaction,
                    cancellationToken);
                await RecordCatalogAsync(connection, transaction, cancellationToken);
                break;

            case UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy:
                throw new UnledgeredSchemaIncompatibleException();

            default:
                throw new InvalidOperationException("Unsupported unledgered database classification.");
        }
    }

    private async Task ApplyWithLedgerAsync(
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
        SqlMigrationLedgerSchemaValidator.Validate(ledgerSchema);

        var history = await _historyStore.ReadAsync(
            connection,
            transaction,
            cancellationToken);
        var prefixLength = SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(
            history,
            _catalog);

        await ExecuteAndRecordAsync(
            connection,
            transaction,
            prefixLength,
            cancellationToken);
    }

    private async Task ExecuteAndRecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int startIndex,
        CancellationToken cancellationToken)
    {
        for (var index = startIndex; index < _catalog.Migrations.Length; index++)
        {
            var migration = _catalog.Migrations[index];
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration,
                cancellationToken);
            await _historyStore.InsertAsync(
                connection,
                transaction,
                migration,
                cancellationToken);
        }
    }

    private async Task RecordCatalogAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var migration in _catalog.Migrations)
        {
            await _historyStore.InsertAsync(
                connection,
                transaction,
                migration,
                cancellationToken);
        }
    }

    private async Task ValidateFinalStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ledgerState = await _ledgerMetadataReader.ResolveObjectAsync(
            connection,
            transaction,
            cancellationToken);

        if (ledgerState.Kind != SqlMigrationLedgerObjectKind.UserTable)
        {
            throw new FinalSchemaValidationException(
                "Final migration validation requires the exact FactoryConnect migration ledger table.");
        }

        var ledgerSchema = await _ledgerMetadataReader.ReadSchemaAsync(
            connection,
            transaction,
            RequireObjectId(ledgerState),
            cancellationToken);
        SqlMigrationLedgerSchemaValidator.Validate(ledgerSchema);

        var history = await _historyStore.ReadAsync(
            connection,
            transaction,
            cancellationToken);
        var prefixLength = SqlMigrationHistoryPrefixValidator.ValidateExactPrefix(history, _catalog);
        if (prefixLength != _catalog.Migrations.Length)
        {
            throw new FinalSchemaValidationException(
                "Final migration history does not exactly match the repository migration catalog.");
        }

        var liveSchema = await _schemaMetadataReader.ReadFactoryConnectOwnedSchemaInTransactionAsync(
            connection,
            transaction,
            cancellationToken);
        var comparison = SqlSchemaComparator.Compare(
            SqlRepositorySchemaDescriptors.Current,
            liveSchema);
        if (!comparison.IsExactMatch)
        {
            throw new FinalSchemaValidationException(
                "Final FactoryConnect-owned SQL schema does not exactly match the current repository descriptor.");
        }
    }

    private static int RequireObjectId(SqlMigrationLedgerObjectState state) =>
        state.ObjectId ?? throw new SqlMigrationLedgerSchemaException(
            "FactoryConnect migration ledger object id is missing for a resolved user table.");
}
