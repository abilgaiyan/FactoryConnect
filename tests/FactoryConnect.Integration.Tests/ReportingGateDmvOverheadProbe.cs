using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

// Opt-in SQL probe. No FactoryConnect stores, gates, runtime, reconstruction or checkpoint operations.
[Trait("Category", "SqlServerIntegration")]
public sealed class ReportingGateDmvOverheadProbe(ITestOutputHelper output)
{
    internal const string ConnectionVariable = "FACTORYCONNECT_GG003_DMV_PROBE_CONNECTION_STRING";
    internal const string AuthorizationVariable = "FACTORYCONNECT_GG003_DMV_PROBE_AUTHORIZATION";
    internal const string AuthorizationValue = "R3.1B-READ-ONLY";
    internal const string CyclesVariable = "FACTORYCONNECT_GG003_DMV_PROBE_CYCLES";
    internal const string PlanVariable = "FACTORYCONNECT_GG003_DMV_PROBE_ESTIMATED_PLANS";
    internal const int CommandTimeoutSeconds = 5;

    internal sealed record Query(string Name, string Sql);
    internal static IReadOnlyList<Query> Queries { get; } = Array.AsReadOnly(new[]
    {
        new Query("sessions", """
            SELECT s.session_id,s.program_name,s.login_time AS session_login_time
            FROM sys.dm_exec_sessions s
            WHERE LEFT(s.program_name,LEN(@Prefix))=@Prefix
            ORDER BY s.session_id;
            """),
        new Query("sessions-requests", """
            SELECT s.session_id,s.program_name,s.login_time AS session_login_time,
                   r.request_id,r.status,r.wait_type,r.wait_time,r.blocking_session_id
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON r.session_id=s.session_id
            WHERE LEFT(s.program_name,LEN(@Prefix))=@Prefix
            ORDER BY s.session_id,r.request_id;
            """),
        new Query("sessions-requests-connections", """
            SELECT s.session_id,s.program_name,s.login_time AS session_login_time,
                   c.connection_id AS server_connection_id,
                   r.request_id,r.status,r.wait_type,r.wait_time,r.blocking_session_id
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_connections c ON c.session_id=s.session_id
            LEFT JOIN sys.dm_exec_requests r ON r.session_id=s.session_id
            WHERE LEFT(s.program_name,LEN(@Prefix))=@Prefix
            ORDER BY s.session_id,r.request_id;
            """),
        new Query("full-existing", """
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
            """),
    });

    [DmvProbeFact]
    public async Task MeasureReadOnlyDmvVariantsWithoutPublication()
    {
        var settings = ValidateTarget(Environment.GetEnvironmentVariable(ConnectionVariable)!,
            Environment.GetEnvironmentVariable(AuthorizationVariable), OperatingSystem.IsWindows());
        var cycles = ParseCycles(Environment.GetEnvironmentVariable(CyclesVariable));
        var plans = Environment.GetEnvironmentVariable(PlanVariable) == "1";
        var application = "FC-Dmv-Overhead-" + Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "artifacts", "TestResults", "DmvOverhead"));
        Directory.CreateDirectory(directory);
        var journalPath = Path.Combine(directory, application + ".jsonl");
        using var journalStream = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var journal = new StreamWriter(journalStream) { AutoFlush = true };
        void Record(object record)
        {
            var json = JsonSerializer.Serialize(record);
            output.WriteLine(json);
            journal.WriteLine(json);
        }
        string Role(string role) => new SqlConnectionStringBuilder(settings.ConnectionString)
        { ApplicationName = application + ":" + role }.ConnectionString;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var succeeded = false;
        try
        {
            Record(new { measurement = "dmv-probe-start", journal = journalPath, variants = Queries.Select(q => q.Name),
                cyclesPerVariant = cycles, estimatedPlansRequested = plans, commandTimeoutSeconds = CommandTimeoutSeconds,
                stopwatchFrequency = Stopwatch.Frequency, database = settings.InitialCatalog,
                applicationIntent = settings.ApplicationIntent.ToString(), pooling = settings.Pooling });
            // A passive anchor makes the application prefix nonempty; it holds no transaction or processor gate.
            await using var anchor = new SqlConnection(Role("anchor"));
            await Stage("anchor", 0, "connection-open", async () => { await anchor.OpenAsync(token); return true; }, Record);
            await using (var guard = anchor.CreateCommand())
            {
                guard.CommandTimeout = CommandTimeoutSeconds;
                guard.CommandText = "SELECT DB_NAME(),CAST(SERVERPROPERTY('IsLocalDB') AS int);";
                await Stage("anchor", 0, "target-verification", async () =>
                {
                    await using var rows = await guard.ExecuteReaderAsync(token);
                    Assert.True(await rows.ReadAsync(token));
                    Assert.Equal(settings.InitialCatalog, rows.GetString(0));
                    Assert.Equal(1, rows.GetInt32(1));
                    return true;
                }, Record);
            }
            foreach (var query in Queries)
            {
                await using var connection = new SqlConnection(Role(query.Name));
                await Stage(query.Name, 0, "connection-open", async () =>
                { await connection.OpenAsync(token); return true; }, Record);
                long? previousCompletion = null;
                for (var cycle = 1; cycle <= cycles; cycle++)
                {
                    var cycleStarted = Stopwatch.GetTimestamp();
                    Record(new { measurement = "dmv-cycle-start", variant = query.Name, cycle,
                        at = cycleStarted, observerSessionId = connection.ServerProcessId,
                        commandFingerprint = ReportingGateCommandTrace.FingerprintSql(query.Sql),
                        sincePreviousCompletionMs = previousCompletion is long previous ?
                            Stopwatch.GetElapsedTime(previous, cycleStarted).TotalMilliseconds : (double?)null });
                    using var command = CreateCommand(connection, query, application + ":");
                    await using var rows = await Stage(query.Name, cycle, "query-execute",
                        () => command.ExecuteReaderAsync(token), Record);
                    var counts = await Stage(query.Name, cycle, "row-stream", async () =>
                    {
                        var total = 0L;
                        var sessions = new HashSet<int>();
                        while (await rows.ReadAsync(token))
                        {
                            total++;
                            sessions.Add(Convert.ToInt32(rows["session_id"], System.Globalization.CultureInfo.InvariantCulture));
                        }
                        return (Total: total, Sessions: sessions.Count);
                    }, Record);
                    var completed = Stopwatch.GetTimestamp();
                    Record(new { measurement = "dmv-cycle-complete", variant = query.Name, cycle,
                        startedAt = cycleStarted, completedAt = completed,
                        milliseconds = Stopwatch.GetElapsedTime(cycleStarted, completed).TotalMilliseconds,
                        rows = counts.Total, distinctSessions = counts.Sessions,
                        rowsPerSession = counts.Sessions == 0 ? (double?)null : (double)counts.Total / counts.Sessions });
                    previousCompletion = completed;
                    // Reader/command disposal occurs before the next iteration's command.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                }
            }
            // Separate opt-in inspection: estimated plans only, no query execution or actual-plan capture.
            if (plans)
                foreach (var query in Queries)
                    await CaptureEstimatedPlan(Role("plan-" + query.Name), query, application + ":",
                        directory, application, Record, token);
            succeeded = true;
        }
        catch (Exception error)
        {
            try
            {
                Record(new { measurement = "dmv-probe-failure", exceptionType = error.GetType().FullName,
                    sqlErrorNumber = (error as SqlException)?.Number, cancelled = error is OperationCanceledException,
                    at = Stopwatch.GetTimestamp() });
                journalStream.Flush(flushToDisk: true);
            }
            catch (Exception journalError)
            {
                output.WriteLine(JsonSerializer.Serialize(new { measurement = "dmv-probe-journal-failure",
                    exceptionType = journalError.GetType().FullName }));
            }
            throw;
        }
        finally
        {
            output.WriteLine("Read-only probe journal: " + journalPath);
            try
            {
                Record(new { measurement = "dmv-probe-stop", succeeded, at = Stopwatch.GetTimestamp() });
                journalStream.Flush(flushToDisk: true);
            }
            catch (Exception error) when (!succeeded)
            {
                output.WriteLine(JsonSerializer.Serialize(new { measurement = "dmv-probe-journal-finalization-failure",
                    exceptionType = error.GetType().FullName }));
            }
        }
    }

    internal static SqlConnectionStringBuilder ValidateTarget(string connectionString, string? authorization, bool isWindows)
    {
        if (!isWindows || authorization != AuthorizationValue)
            throw new InvalidOperationException("Explicit Windows LocalDB read-only probe authorization is required.");
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!settings.DataSource.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase) ||
            !settings.InitialCatalog.StartsWith("FactoryConnect_GG003_DMV_RCA_", StringComparison.Ordinal))
            throw new InvalidOperationException("A separate DMV_RCA LocalDB restore is required.");
        settings.ApplicationIntent = ApplicationIntent.ReadOnly;
        settings.Pooling = false; // Avoid retaining prior variant sessions in comparison results.
        settings.ConnectTimeout = 5;
        return settings;
    }

    internal static int ParseCycles(string? value)
    {
        if (value is null) return 3;
        if (!int.TryParse(value, out var cycles) || cycles is < 1 or > 10)
            throw new InvalidOperationException("Probe cycles must be between 1 and 10.");
        return cycles;
    }

    internal static SqlCommand CreateCommand(SqlConnection connection, Query query, string prefix)
    {
        var command = connection.CreateCommand();
        command.CommandText = query.Sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.Add("@Prefix", SqlDbType.NVarChar, 128).Value = prefix;
        return command;
    }

    internal static async Task<T> Stage<T>(string variant, int cycle, string stage, Func<Task<T>> action, Action<object> record)
    {
        var started = Stopwatch.GetTimestamp();
        record(new { measurement = "dmv-stage-start", variant, cycle, stage, startedAt = started });
        try
        {
            var result = await action();
            var completed = Stopwatch.GetTimestamp();
            record(new { measurement = "dmv-stage-complete", variant, cycle, stage, startedAt = started,
                completedAt = completed, milliseconds = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds });
            return result;
        }
        catch (Exception error)
        {
            var failedAt = Stopwatch.GetTimestamp();
            try
            {
                record(new { measurement = "dmv-stage-failure", variant, cycle, stage, startedAt = started, failedAt,
                    milliseconds = Stopwatch.GetElapsedTime(started, failedAt).TotalMilliseconds,
                    exceptionType = error.GetType().FullName, sqlErrorNumber = (error as SqlException)?.Number,
                    innerExceptionType = error.InnerException?.GetType().FullName });
            }
            catch { /* Outer probe handler reports journal failure and preserves this original exception. */ }
            throw;
        }
    }

    private static async Task CaptureEstimatedPlan(string connectionString, Query query, string prefix,
        string directory, string application, Action<object> record, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await Stage(query.Name, 0, "plan-connection-open", async () => { await connection.OpenAsync(token); return true; }, record);
        using (var enable = connection.CreateCommand())
        {
            enable.CommandTimeout = CommandTimeoutSeconds;
            enable.CommandText = "SET SHOWPLAN_XML ON;";
            await Stage(query.Name, 0, "estimated-plan-enable", () => enable.ExecuteNonQueryAsync(token), record);
        }
        // No pooling: disposing the connection also discards the session-only SHOWPLAN setting.
        using var command = CreateCommand(connection, query, prefix);
        await using var rows = await Stage(query.Name, 0, "estimated-plan-read", () => command.ExecuteReaderAsync(token), record);
        var plans = await Stage(query.Name, 0, "estimated-plan-stream", async () =>
        {
            var result = new List<string>();
            while (await rows.ReadAsync(token))
                result.Add(SanitizeEstimatedPlan(rows.GetSqlXml(0).Value, prefix));
            return result;
        }, record);
        if (plans.Count == 0) throw new InvalidDataException("Estimated plan was not returned.");
        for (var i = 0; i < plans.Count; i++)
        {
            var path = Path.Combine(directory, application + "-" + query.Name + "-" + i + ".sqlplan");
            await File.WriteAllTextAsync(path, plans[i], token);
            record(new { measurement = "estimated-plan-artifact", variant = query.Name, path, estimatedOnly = true });
        }
    }

    internal static string SanitizeEstimatedPlan(string xml, string prefix)
    {
        var document = XDocument.Parse(xml);
        foreach (var attribute in document.Descendants().Attributes().ToArray())
        {
            if (attribute.Name.LocalName is "ParameterCompiledValue" or "ParameterRuntimeValue")
                attribute.Remove();
            else attribute.Value = attribute.Value.Replace(prefix, "[probe-prefix]", StringComparison.Ordinal);
        }
        foreach (var node in document.DescendantNodes().OfType<XText>())
            node.Value = node.Value.Replace(prefix, "[probe-prefix]", StringComparison.Ordinal);
        return document.ToString();
    }

    public sealed class DmvProbeFactAttribute : FactAttribute
    {
        public DmvProbeFactAttribute()
        {
            if (!OperatingSystem.IsWindows() ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AuthorizationVariable)))
                Skip = "Read-only SQL probe requires a separate authorized DMV_RCA LocalDB restore; code review alone does not authorize execution.";
        }
    }
}
