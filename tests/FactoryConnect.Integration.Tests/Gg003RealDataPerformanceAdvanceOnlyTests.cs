using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

public sealed partial class Gg003RealDataPerformanceTests
{
    [AdvanceOnlyFact]
    public async Task AdvanceOnly534To537WithMeasuredHistoricalSqlAndConcurrentReporting()
    {
        var connectionString = Environment.GetEnvironmentVariable(Variable)!;
        RequireTarget(connectionString);
        Assert.Equal("ADVANCE534_TO537", Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_PERF_ADVANCE_CONFIRM"));
        var root = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_PERF_REPO_ROOT")
            ?? throw new InvalidOperationException("Supply the developer repository root."),
            "artifacts", "TestResults", "gg003-pr152-validation", "advance-only"));
        Directory.CreateDirectory(root);
        var report = Path.Combine(root, "AdvanceOnly534To537-" + Guid.NewGuid().ToString("N") + ".jsonl");
        async Task Record(object value) => await File.AppendAllTextAsync(report,
            JsonSerializer.Serialize(value) + Environment.NewLine);
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = budget.Token;
        using var counter = new ContainmentHistoricalSqlCounter(connectionString);
        using var reportingCounter = new ContainmentHistoricalSqlCounter(connectionString);
        using var replayCounter = new ContainmentHistoricalSqlCounter(connectionString);
        await using var connection = new SqlConnection(connectionString);
        var correctness = "NOT_COMPLETED";
        var instrumentation = "NOT_COMPLETED";
        var coverage = "NOT_ATTEMPTED";
        var reportingSucceeded = false;
        await Record(new { Experiment = "AdvanceOnly534To537", Phase = "Start", Database,
            SourceAssembly = typeof(Gg003RealDataPerformanceTests).Assembly.ManifestModule.ModuleVersionId,
            RuntimeVersion = Environment.Version.ToString(), BootstrapExecuted = false,
            ExclusiveOwnershipMeasured = false, SharedGateWaitMeasured = false, PeakMemoryMeasured = false });
        try
        {
            await connection.OpenAsync(ct);
            await using (var identity = connection.CreateCommand())
            {
                identity.CommandText = "SELECT DB_NAME();";
                Assert.Equal(Database, (string)(await identity.ExecuteScalarAsync(ct))!);
            }
            await AssertMigration21(connection, ct);
            Assert.Equal(524m, await Legacy(connection, ct));
            var store = new SqlServerOperationalMetricProjectionStore(connectionString);
            var at534 = await store.ReadCheckpointAsync(Processor, Stream, ct);
            Assert.NotNull(at534); Assert.Equal(534UL, at534.SourceRevision.Position.Value);
            var aggregation = new SqlServerMetricAggregationStore(connectionString);
            var head = await aggregation.ReadCheckpointAsync(SourceProcessor, Stream, ct);
            Assert.NotNull(head); Assert.Equal(741UL, head.Position.Value);
            Assert.NotNull(await aggregation.ReadExactAsync(new(SourceProcessor, Stream, new(537)), ct));
            var next = await aggregation.ReadNextAsync(SourceProcessor, Stream, at534.SourceRevision, ct);
            Assert.NotNull(next); Assert.Equal(537UL, next.Revision.Position.Value);

            var sourceAndLegacyBefore = await Gg003BoundedContainmentCatchupAcceptanceTests.Fingerprint(connection);
            var containmentBefore = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null);
            var witnessesBefore = await ArchivedWitnesses(connection, ct);
            var witness534 = await Witness(store, ct);
            Assert.Equal(380UL, witness534.Contribution.Position.Value);
            Assert.Equal("05A4224CDC5CBBD1AC23A19F9DEBD3B62AB450EAE0501D00AB06237037411824", witness534.Contribution.Fact.Id.Value);
            Assert.Equal(2683.614157m, witness534.Numerator.Aggregate.Value);
            Assert.Equal(2452.013355m, witness534.Denominator.Aggregate.Value);
            await Record(new { Experiment = "AdvanceOnly534To537", Phase = "Baseline", Containment = 534,
                Legacy = 524, Aggregation = 741, SourceAndLegacyFingerprint = sourceAndLegacyBefore,
                ContainmentFingerprint = Hash(containmentBefore),
                WitnessFingerprint = Hash(JsonSerializer.Serialize(witnessesBefore)),
                HistoricalTimingBoundary = "SqlClient Before to After/Error; excludes reader drain/materialization" });

            var runtime = Runtime(counter.ConnectionString, 537);
            var operationStart = Stopwatch.GetTimestamp();
            long operationEnd = 0;
            async Task<int> Advance()
            {
                try { return await runtime.RunCycleAsync(ct); }
                finally { Interlocked.Exchange(ref operationEnd, Stopwatch.GetTimestamp()); }
            }
            var cycle = Advance();
            var observation = Stopwatch.StartNew();
            while (!cycle.IsCompleted && counter.PendingCount == 0 && observation.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(5); // No database sampler, writer pause, or cancellation that abandons cycle.
            var pendingAtRequest = counter.PendingCount > 0;
            var requestStart = Stopwatch.GetTimestamp();
            long requestEnd = 0;
            ulong? reportingRevision = null;
            string? reportingError = null;
            async Task ReportDuringAdvance()
            {
                try
                {
                    var checkpoint = await new SqlServerOperationalMetricProjectionStore(reportingCounter.ConnectionString)
                        .ReadCheckpointAsync(Processor, Stream, ct);
                    reportingRevision = checkpoint?.SourceRevision.Position.Value;
                    reportingSucceeded = reportingRevision is 534UL or 537UL;
                }
                catch (Exception error) { reportingError = error.GetType().FullName; }
                finally { Interlocked.Exchange(ref requestEnd, Stopwatch.GetTimestamp()); }
            }
            var reporting = ReportDuringAdvance();
            int count;
            try { count = await cycle; }
            finally
            {
                // Always await the read, including a failed advancement. Never leave a SQL operation running.
                await reporting;
                coverage = HasExecutionOverlap(operationStart, operationEnd, requestStart, requestEnd) ? "PASS" : "MISSED";
                await Record(new { Experiment = "AdvanceOnly534To537", Phase = "MeasuredOperation",
                    CycleMilliseconds = Stopwatch.GetElapsedTime(operationStart, operationEnd).TotalMilliseconds,
                    ReportingMilliseconds = Stopwatch.GetElapsedTime(requestStart, requestEnd).TotalMilliseconds,
                    ReportingStartOffsetMilliseconds = Stopwatch.GetElapsedTime(operationStart, requestStart).TotalMilliseconds,
                    ReportingEndOffsetMilliseconds = Stopwatch.GetElapsedTime(operationStart, requestEnd).TotalMilliseconds,
                    ReportingRevision = reportingRevision, ReportingErrorType = reportingError,
                    ReportingSucceeded = reportingSucceeded, ConcurrencyCoverage = coverage,
                    OverlapBoundary = "Observed runtime RunCycleAsync and reporting request invocation lifetimes",
                    PendingHistoricalAtRequest = pendingAtRequest,
                    RuntimeHistorical = AdvanceCounterSummary(counter), ReportingHistorical = AdvanceCounterSummary(reportingCounter),
                    ExclusiveOwnershipMeasured = false, SharedGateWaitMeasured = false });
            }

            Assert.True(count > 0);
            var at537 = await store.ReadCheckpointAsync(Processor, Stream, ct);
            Assert.NotNull(at537); Assert.Equal(537UL, at537.SourceRevision.Position.Value);
            Assert.Equal(count, at537.BatchManifest.ProjectionKeys.Count);
            var projections = new List<OperationalMetricProjection>();
            foreach (var key in at537.BatchManifest.ProjectionKeys)
            {
                var projection = await store.ReadProjectionAsync(Processor, key, ct);
                Assert.NotNull(projection);
                Assert.Equal(at537.SourceRevision, projection.SourceRevision);
                Assert.Equal(AvailabilityContainmentV1.PolicyId, projection.EvaluationPolicyId);
                projections.Add(projection);
            }
            _ = await Witness(store, ct);
            var witnessesAfter = await ArchivedWitnesses(connection, ct);
            Assert.All(witnessesBefore, row => Assert.Contains(row, witnessesAfter));
            var beforeReplay = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null);
            var replayClock = Stopwatch.StartNew();
            await new SqlServerOperationalMetricContainmentPublisher(replayCounter.ConnectionString)
                .PublishAsync(at537, at537, projections, ct);
            await Record(new { Experiment = "AdvanceOnly534To537", Phase = "ExactReplay537",
                PublicationMilliseconds = replayClock.Elapsed.TotalMilliseconds, Historical = AdvanceCounterSummary(replayCounter) });
            Assert.Equal(beforeReplay, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null));
            Assert.Equal(524m, await Legacy(connection, ct));
            Assert.Equal(sourceAndLegacyBefore, await Gg003BoundedContainmentCatchupAcceptanceTests.Fingerprint(connection));
            var finalHead = await aggregation.ReadCheckpointAsync(SourceProcessor, Stream, ct);
            Assert.NotNull(finalHead); Assert.Equal(741UL, finalHead.Position.Value);
            var durable = await store.ReadCheckpointAsync(Processor, Stream, ct);
            Assert.NotNull(durable); Assert.Equal(537UL, durable.SourceRevision.Position.Value);
            correctness = "PASS";
            await Record(new { Experiment = "AdvanceOnly534To537", Phase = "Correctness", Correctness = correctness,
                Containment = 537, Legacy = 524, Aggregation = 741, ProjectionCount = count,
                ContainmentFingerprint = Hash(beforeReplay), WitnessFingerprint = Hash(JsonSerializer.Serialize(witnessesAfter)),
                SourceAndLegacyPreserved = true, RetainedWitnessesPreserved = true, ExactReplayUnchanged = true });

            instrumentation = "FAIL";
            ValidateCounter(counter); ValidateCounter(replayCounter);
            Assert.All(counter.Executions.GroupBy(e => (e.TransactionOrdinal, e.Processor, e.Stream, e.Position)), group => Assert.Single(group));
            Assert.All(replayCounter.Executions.GroupBy(e => (e.TransactionOrdinal, e.Processor, e.Stream, e.Position)), group => Assert.Single(group));
            Assert.Null(reportingCounter.ListenerError); Assert.Equal(0, reportingCounter.PendingCount);
            // A failed read may have no historical commands. Validate every observed command's binding,
            // and keep the request outcome separately visible in the evidence.
            foreach (var execution in reportingCounter.Executions)
            {
                Assert.True(execution.HadTransactionAtStart); Assert.True(execution.TransactionBoundToConnectionAtStart);
                Assert.True(execution.TransactionOrdinal > 0); Assert.NotNull(execution.Elapsed);
                Assert.False(execution.Failed); Assert.True(execution.Elapsed.Value >= TimeSpan.Zero);
            }
            Assert.All(reportingCounter.Executions.GroupBy(e => (e.TransactionOrdinal, e.Processor, e.Stream, e.Position)), group => Assert.Single(group));
            if (reportingSucceeded) Assert.NotEmpty(reportingCounter.Executions);
            instrumentation = "PASS";
        }
        catch (Exception error)
        {
            await Record(new { Experiment = "AdvanceOnly534To537", Phase = "Failed",
                ErrorType = error.GetType().FullName, AutomaticRetryAllowed = false });
            // Client failures may occur after commit. Read raw durable state without invoking the
            // runtime or repairing anything; record unknown if even reconciliation cannot complete.
            try
            {
                using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using var readback = new SqlConnection(connectionString);
                await readback.OpenAsync(reconcile.Token);
                await using var command = readback.CreateCommand();
                command.CommandText = """
                    SELECT c.Position FROM dbo.OperationalMetricProjectionCheckpoint c
                    JOIN dbo.OperationalMetricProjectionProcessor p
                      ON p.OperationalMetricProjectionProcessorRowId=c.OperationalMetricProjectionProcessorRowId
                    WHERE p.ProcessorKey=@Processor;
                    """;
                command.Parameters.AddWithValue("@Processor", Processor.Value);
                var position = await command.ExecuteScalarAsync(reconcile.Token);
                await Record(new { Experiment = "AdvanceOnly534To537", Phase = "Reconciliation",
                    DurableContainment = position is decimal value ? (decimal?)value : null, ReadSucceeded = true });
            }
            catch (Exception readError)
            {
                await Record(new { Experiment = "AdvanceOnly534To537", Phase = "Reconciliation",
                    ReadSucceeded = false, ErrorType = readError.GetType().FullName });
            }
            throw;
        }
        finally
        {
            await Record(new { Experiment = "AdvanceOnly534To537", Phase = "Final",
                Correctness = correctness, Instrumentation = instrumentation, ConcurrencyCoverage = coverage,
                ReportingSucceeded = reportingSucceeded,
                RuntimeHistorical = AdvanceCounterSummary(counter), ReportingHistorical = AdvanceCounterSummary(reportingCounter),
                ReplayHistorical = AdvanceCounterSummary(replayCounter), AutomaticRetryAllowed = false,
                PerformanceAccepted = false, ExclusiveOwnershipMeasured = false, SharedGateWaitMeasured = false, PeakMemoryMeasured = false });
        }
    }

    internal static bool HasExecutionOverlap(long operationStart, long operationEnd, long requestStart, long requestEnd) =>
        operationEnd > operationStart && requestEnd > requestStart &&
        requestStart < operationEnd && operationStart < requestEnd;

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(value)));

    private static object AdvanceCounterSummary(ContainmentHistoricalSqlCounter counter) =>
        new { ExecutionCount = counter.Executions.Length, Diagnostic = Summary(counter) };

    private static async Task AssertMigration21(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM dbo.FactoryConnectMigrationHistory ORDER BY MigrationId;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<int>();
        while (await reader.ReadAsync(ct)) ids.Add(Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Enumerable.Range(1, 21), ids);
    }

    public sealed class AdvanceOnlyFactAttribute : FactAttribute
    {
        public AdvanceOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)) ||
                Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_PERF_ADVANCE_CONFIRM") != "ADVANCE534_TO537")
                Skip = "Requires separate advance-only opt-in; consumes this disposable clone's revision-534 starting state.";
        }
    }
}

public sealed class Gg003AdvanceOnlyOverlapTests
{
    [Theory]
    [InlineData(10, 30, 20, 40, true)]
    [InlineData(10, 20, 20, 30, false)]
    [InlineData(10, 20, 30, 40, false)]
    [InlineData(10, 0, 15, 25, false)]
    public void RequiresMeasuredIntervalsToIntersect(long start, long end, long readStart, long readEnd, bool expected) =>
        Assert.Equal(expected, Gg003RealDataPerformanceTests.HasExecutionOverlap(start, end, readStart, readEnd));
}
