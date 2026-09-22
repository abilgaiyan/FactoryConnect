using FactoryConnect.Persistence;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerPersistenceStartupGate : IPersistenceStartupGate
{
    private readonly string _connectionString;
    private readonly SqlPersistenceStartupOptions _options;
    private readonly Func<string, TimeSpan, CancellationToken, Task<SqlRuntimeCompatibilityResult>> _verificationStage;

    public SqlServerPersistenceStartupGate(
        string connectionString,
        SqlPersistenceStartupOptions options)
        : this(
            connectionString,
            options,
            RunVerificationAsync)
    {
    }

    internal SqlServerPersistenceStartupGate(
        string connectionString,
        SqlPersistenceStartupOptions options,
        Func<string, TimeSpan, CancellationToken, Task<SqlRuntimeCompatibilityResult>> verificationStage)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQL Server connection string is required.", nameof(connectionString));
        }

        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verificationStage);

        _connectionString = connectionString;
        _options = options;
        _verificationStage = verificationStage;
    }

    internal TimeSpan LockTimeout => _options.LockTimeout;

    public async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
