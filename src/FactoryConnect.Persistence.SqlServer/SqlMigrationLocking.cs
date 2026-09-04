using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlMigrationLockAcquisitionException : Exception
{
    public SqlMigrationLockAcquisitionException(int returnCode)
        : base($"Failed to acquire the FactoryConnect SQL migration lock. sp_getapplock returned {returnCode}.")
    {
        ReturnCode = returnCode;
    }

    public int ReturnCode { get; }
}

internal static class SqlMigrationLockTimeout
{
    public static int ToMilliseconds(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Migration lock timeout must be finite and non-negative.");
        }

        var maximumTicks = checked((long)int.MaxValue * TimeSpan.TicksPerMillisecond);
        if (timeout.Ticks > maximumTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Migration lock timeout exceeds the SQL Server Int32 millisecond limit.");
        }

        if (timeout == TimeSpan.Zero)
        {
            return 0;
        }

        return checked((int)((timeout.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond));
    }
}

internal sealed class SqlServerMigrationTransactionScope : IAsyncDisposable
{
    public const string LockResource = "FactoryConnect.SqlMigration";

    private readonly SqlConnection _connection;
    private bool _completed;

    private SqlServerMigrationTransactionScope(
        SqlConnection connection,
        SqlTransaction transaction)
    {
        _connection = connection;
        Transaction = transaction;
    }

    public SqlTransaction Transaction { get; }

    public static async Task<SqlServerMigrationTransactionScope> BeginAsync(
        SqlConnection connection,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("SQL migration connection must already be open.");
        }

        var lockTimeoutMilliseconds = SqlMigrationLockTimeout.ToMilliseconds(lockTimeout);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var returnCode = await AcquireLockAsync(
                connection,
                transaction,
                lockTimeoutMilliseconds,
                cancellationToken);

            if (returnCode < 0)
            {
                throw new SqlMigrationLockAcquisitionException(returnCode);
            }

            return new SqlServerMigrationTransactionScope(connection, transaction);
        }
        catch
        {
            await RollbackBestEffortAsync(transaction);
            await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        await Transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        await Transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await RollbackBestEffortAsync(Transaction);
            _completed = true;
        }

        await Transaction.DisposeAsync();
    }

    private static async Task<int> AcquireLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int lockTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @ReturnCode int;
            EXEC @ReturnCode = sys.sp_getapplock
                @Resource = @Resource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = @LockTimeout,
                @DbPrincipal = N'public';
            SELECT @ReturnCode;
            """;
        command.Parameters.Add(new SqlParameter("@Resource", System.Data.SqlDbType.NVarChar, 255)
        {
            Value = LockResource,
        });
        command.Parameters.Add(new SqlParameter("@LockTimeout", System.Data.SqlDbType.Int)
        {
            Value = lockTimeoutMilliseconds,
        });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task RollbackBestEffortAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The transaction may already have been completed by SQL Server.
        }
        catch (SqlException)
        {
            // Cleanup must not replace the primary migration-lock failure.
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("SQL migration transaction scope has already completed.");
        }

        if (!ReferenceEquals(Transaction.Connection, _connection))
        {
            throw new InvalidOperationException("SQL migration transaction is no longer associated with its deployment connection.");
        }
    }
}
