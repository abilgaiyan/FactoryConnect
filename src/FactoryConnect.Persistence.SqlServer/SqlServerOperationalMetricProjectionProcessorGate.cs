using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal enum OperationalMetricProjectionProcessorGateMode { Shared, Exclusive }

/// <summary>Concurrency coordination only; never substitutes persisted identity validation.</summary>
internal static class SqlServerOperationalMetricProjectionProcessorGate
{
    internal const int DefaultTimeoutMilliseconds = 15000;
    internal static string EncodeResource(OperationalMetricProjectionProcessorId processorId)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        return "FactoryConnect:OperationalMetricProjection:Processor:v1:" +
            Convert.ToHexString(SHA256.HashData(StringOrderKeyV2Codec.Encode(processorId.Value)));
    }

    internal static async Task AcquireAsync(SqlConnection connection, SqlTransaction transaction,
        OperationalMetricProjectionProcessorId processorId, OperationalMetricProjectionProcessorGateMode mode,
        CancellationToken cancellationToken, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMilliseconds);
        if (transaction.Connection != connection || connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The processor gate requires this connection's active transaction.");
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = checked((timeoutMilliseconds / 1000) + 5);
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock " +
            "@Resource=@Resource, @LockMode=@Mode, @LockOwner='Transaction', " +
            "@DbPrincipal='public', @LockTimeout=@Timeout; SELECT @result;";
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = EncodeResource(processorId);
        command.Parameters.Add("@Mode", SqlDbType.VarChar, 32).Value = mode.ToString();
        command.Parameters.Add("@Timeout", SqlDbType.Int).Value = timeoutMilliseconds;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (result == -2) throw new OperationCanceledException("Processor gate acquisition cancelled.", cancellationToken);
        if (result == -1) throw new TimeoutException("Processor gate acquisition timed out.");
        if (result < 0) throw new InvalidOperationException($"Processor gate acquisition failed with return code {result}.");
    }
}
