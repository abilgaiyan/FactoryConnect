using System.Data;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>
/// Fixture-scoped SQL application lock. A session lock coordinates cooperating Gate B
/// writers but does not by itself authorize ingestion or protect against external writers.
/// </summary>
internal sealed class GateBExclusiveWriterLease : IAsyncDisposable
{
    private const string Resource = "FactoryConnect:GateB:MOXA-001:20261110";
    private readonly SqlConnection _connection;

    private GateBExclusiveWriterLease(SqlConnection connection) => _connection = connection;

    public static async Task<GateBExclusiveWriterLease> AcquireAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @Resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = 0,
                    @DbPrincipal = 'public';
                SELECT @result;
                """;
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = Resource;
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
                throw new InvalidOperationException("Gate B fixture writer lease is already held or unavailable.");
            return new GateBExclusiveWriterLease(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_releaseapplock
                    @Resource = @Resource,
                    @LockOwner = 'Session',
                    @DbPrincipal = 'public';
                SELECT @result;
                """;
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = Resource;
            _ = await command.ExecuteScalarAsync();
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
