using FactoryConnect.Persistence;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerPersistenceStartupGate : IPersistenceStartupGate
{
    private readonly string _connectionString;
    private readonly SqlPersistenceStartupOptions _options;
    private readonly Func<string, TimeSpan, CancellationToken, Task> _migrationStage;
    private readonly Func<string, TimeSpan, CancellationToken, Task<SqlRuntimeCompatibilityResult>> _verificationStage;

    public SqlServerPersistenceStartupGate(
        string connectionString,
        SqlPersistenceStartupOptions options)
        : this(
            connectionString,
            options,
            RunMigrationAsync,
            RunVerificationAsync)
    {
    }

    internal SqlServerPersistenceStartupGate(
        string connectionString,
        SqlPersistenceStartupOptions options,
        Func<string, TimeSpan, CancellationToken, Task> migrationStage,
        Func<string, TimeSpan, CancellationToken, Task<SqlRuntimeCompatibilityResult>> verificationStage)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQL Server connection string is required.", nameof(connectionString));
        }

        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(migrationStage);
        ArgumentNullException.ThrowIfNull(verificationStage);

        _connectionString = connectionString;
        _options = options;
        _migrationStage = migrationStage;
        _verificationStage = verificationStage;
    }

    internal TimeSpan LockTimeout => _options.LockTimeout;

    public async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _migrationStage(
                _connectionString,
                _options.LockTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (SqlPersistenceStartupCancellationPolicy.MustPropagate(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SqlPersistenceStartupException.MigrationOperationalFailure(exception);
        }

        SqlRuntimeCompatibilityResult compatibilityResult;
        try
        {
            compatibilityResult = await _verificationStage(
                _connectionString,
                _options.LockTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (SqlPersistenceStartupCancellationPolicy.MustPropagate(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SqlPersistenceStartupException.VerificationOperationalFailure(exception);
        }

        if (!compatibilityResult.IsCompatible)
        {
            throw SqlPersistenceStartupException.DatabaseIncompatible(compatibilityResult);
        }
    }

    private static async Task RunMigrationAsync(
        string connectionString,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var engine = SqlServerMigrationEngine.CreateDefault();
        await engine.ApplyAsync(connection, lockTimeout, cancellationToken);
    }

    private static async Task<SqlRuntimeCompatibilityResult> RunVerificationAsync(
        string connectionString,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var verifier = SqlServerRuntimeSchemaCompatibilityVerifier.CreateDefault();
        return await verifier.VerifyAsync(connection, lockTimeout, cancellationToken);
    }
}
