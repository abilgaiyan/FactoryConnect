using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Sampled DMV evidence, not exact grant/release tracing. Never reads SQL text.
internal sealed class ReportingGateWaitSampler(string connectionString, string application)
{
    private readonly ConcurrentQueue<string> _samples = new();
    private int _sampleCount;
    private int _rows;
    private long _started;
    private long _finished;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task RunAsync(CancellationToken token)
    {
        _started = Stopwatch.GetTimestamp();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = """
                    SELECT s.session_id,s.program_name,s.login_time AS session_login_time,c.connection_id AS server_connection_id,r.request_id,
                           r.status,r.wait_type,r.wait_time,r.blocking_session_id,
                           r.transaction_id AS server_transaction_id,st.transaction_id AS server_session_transaction_id,
                           s.open_transaction_count,l.resource_description,l.request_mode,
                           l.request_status,l.request_owner_id AS server_lock_owner_id
                    FROM sys.dm_exec_sessions s
                    LEFT JOIN sys.dm_exec_connections c ON c.session_id=s.session_id
                    LEFT JOIN sys.dm_exec_requests r ON r.session_id=s.session_id
                    LEFT JOIN sys.dm_tran_session_transactions st ON st.session_id=s.session_id
                    LEFT JOIN sys.dm_tran_locks l ON l.request_session_id=s.session_id
                         AND l.resource_type='APPLICATION' AND l.resource_database_id=DB_ID()
                    WHERE LEFT(s.program_name,LEN(@Prefix))=@Prefix
                    ORDER BY s.session_id,r.request_id;
                    """;
                command.Parameters.AddWithValue("@Prefix", application + ":");
                var began = Stopwatch.GetTimestamp();
                await using var rows = await command.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token))
                {
                    var data = new Dictionary<string, object?>
                    {
                        ["measurement"] = "sql-wait-sample",
                        ["sampleStartedAt"] = began,
                        ["observedAt"] = Stopwatch.GetTimestamp(),
                    };
                    for (var i = 0; i < rows.FieldCount; i++)
                        data[rows.GetName(i)] = rows.IsDBNull(i) ? null : rows.GetValue(i);
                    _rows++;
                    if (_samples.Count < 5000) _samples.Enqueue(JsonSerializer.Serialize(data));
                }
                _sampleCount++;
                Ready.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Ready.TrySetException(error);
            throw; // Missing DMV permission is a failed measurement, never an empty-wait conclusion.
        }
        finally { _finished = Stopwatch.GetTimestamp(); }
    }

    internal void Write(ITestOutputHelper output)
    {
        foreach (var sample in _samples) output.WriteLine(sample);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            measurement = "sql-wait-sampling-coverage",
            startedAt = _started, finishedAt = _finished,
            samples = _sampleCount, observedRows = _rows, retainedRows = _samples.Count,
            nominalIntervalMs = 250, truncated = _rows > _samples.Count,
        }));
        output.WriteLine("DMVs are sampled after query overhead; short waits may be missed. No observed wait does not prove no blocking. Application-lock resources may be truncated by SQL Server.");
    }
}
