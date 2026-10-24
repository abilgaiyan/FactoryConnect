using System.Data;
using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

// Opt-in only. No migrations, restores, source writes, host startup or production changes.
[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003RealDataPerformanceTests
{
    internal const string Database = "FactoryConnect_GG003_STAGEB_REPLAY_PR152_PERF_20261024";
    private const string Variable = "FACTORYCONNECT_GG003_PERF_CONNECTION_STRING";
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private static readonly MetricInputStreamId Stream = MetricInputStreamId.ForMachine(Machine);
    private static readonly MetricAggregationProcessorId SourceProcessor = new($"metric-aggregation:{Machine.Value}");
    private static readonly OperationalMetricProjectionProcessorId Processor = AvailabilityContainmentV1.ProjectionProcessorId(Machine);

    internal static void RequireTarget(string connectionString)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!settings.DataSource.Equals("(localdb)\\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase) ||
            !settings.InitialCatalog.Equals(Database, StringComparison.Ordinal) ||
            !settings.IntegratedSecurity || !string.IsNullOrEmpty(settings.AttachDBFilename))
            throw new InvalidOperationException("Performance harness requires the exact disposable developer LocalDB database.");
    }

    [PerformanceFact]
    public async Task Bootstrap534ThenAdvance537WithMeasuredHistoricalSqlAndConcurrentReporting()
    {
        var connectionString = Environment.GetEnvironmentVariable(Variable)!;
        RequireTarget(connectionString); // Validate before opening any connection.
        Assert.Equal("BOOTSTRAP534_ADVANCE537", Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_PERF_CONFIRM"));
        var root = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_PERF_REPO_ROOT")
            ?? throw new InvalidOperationException("Supply the developer repository root."), "artifacts", "TestResults", "gg003-pr152-validation", "stage-b"));
        Directory.CreateDirectory(root);
        var report = Path.Combine(root, "performance-" + Guid.NewGuid().ToString("N") + ".jsonl");
        async Task Record(object value) => await File.AppendAllTextAsync(report, JsonSerializer.Serialize(value) + Environment.NewLine);
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        var ct = budget.Token;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DB_NAME();";
            Assert.Equal(Database, (string)(await command.ExecuteScalarAsync(ct))!);
        }
        var store = new SqlServerOperationalMetricProjectionStore(connectionString);
        // Fail closed on previously bootstrapped clones. Never resume this experiment automatically.
        Assert.Null(await store.ReadCheckpointAsync(Processor, Stream, ct));
        Assert.Equal(524m, await Legacy(connection, ct));
        var aggregation = new SqlServerMetricAggregationStore(connectionString);
        Assert.NotNull(await aggregation.ReadExactAsync(new(SourceProcessor, Stream, new(534)), ct));
        Assert.NotNull(await aggregation.ReadExactAsync(new(SourceProcessor, Stream, new(537)), ct));
        var nextAfter534 = await aggregation.ReadNextAsync(SourceProcessor, Stream, new(SourceProcessor, Stream, new(534)), ct);
        Assert.NotNull(nextAfter534); Assert.Equal(537UL, nextAfter534.Revision.Position.Value);
        var before = await Gg003BoundedContainmentCatchupAcceptanceTests.Fingerprint(connection);
        await Record(new { Phase = "Preflight", Database, ExpectedLegacy = 524, Bootstrap = 534, Target = 537,
            TimingBoundary = "SqlClient Before to After/Error ExecuteReader events; excludes subsequent reader drain/materialization",
            ExclusiveOwnershipMeasured = false, SharedGateWaitMeasured = false,
            Runtime = Environment.Version.ToString(), ProcessId = Environment.ProcessId,
            SourceAssembly = typeof(Gg003RealDataPerformanceTests).Assembly.ManifestModule.ModuleVersionId,
            Report = report });
        try
        {
            using var bootstrapCounter = new ContainmentHistoricalSqlCounter(connectionString);
            var bootstrap = Runtime(bootstrapCounter.ConnectionString, 534);
            var bootstrapClock = Stopwatch.StartNew();
            for (var cycle = 0; cycle < 10_000; cycle++)
            {
                Assert.True(await bootstrap.RunCycleAsync(ct) > 0);
                var checkpoint = await store.ReadCheckpointAsync(Processor, Stream, ct);
                Assert.NotNull(checkpoint);
                Assert.True(checkpoint.SourceRevision.Position.Value <= 534);
                if (checkpoint.SourceRevision.Position.Value == 534) break;
            }
            var at534 = await store.ReadCheckpointAsync(Processor, Stream, ct);
            Assert.NotNull(at534); Assert.Equal(534UL, at534.SourceRevision.Position.Value);
            await Record(new { Phase = "Bootstrap534", TotalMilliseconds = bootstrapClock.Elapsed.TotalMilliseconds,
                Historical = Summary(bootstrapCounter) });
            ValidateCounter(bootstrapCounter);
            var witness534 = await Witness(store, ct);
            var archivedWitnesses534 = await ArchivedWitnesses(connection, ct);
            Assert.Equal(380UL, witness534.Contribution.Position.Value);
            Assert.Equal("05A4224CDC5CBBD1AC23A19F9DEBD3B62AB450EAE0501D00AB06237037411824", witness534.Contribution.Fact.Id.Value);
            Assert.Equal(2683.614157m, witness534.Numerator.Aggregate.Value);
            Assert.Equal(2452.013355m, witness534.Denominator.Aggregate.Value);

            using var counter = new ContainmentHistoricalSqlCounter(connectionString);
            using var reportingCounter = new ContainmentHistoricalSqlCounter(connectionString);
            var advance = Runtime(counter.ConnectionString, 537);
            var clock = Stopwatch.StartNew();
            var cycleTask = advance.RunCycleAsync(ct).AsTask();
            // Observe a pending historical read without adding a database sampler or delaying the writer.
            var observation = Stopwatch.StartNew();
            while (!cycleTask.IsCompleted && counter.PendingCount == 0 && observation.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(5);
            var startedWhileCycleActive = !cycleTask.IsCompleted;
            var pendingHistoricalAtRequest = counter.PendingCount > 0;
            var requestStartMs = clock.Elapsed.TotalMilliseconds;
            async Task<object> Reporting()
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    var checkpoint = await new SqlServerOperationalMetricProjectionStore(reportingCounter.ConnectionString)
                        .ReadCheckpointAsync(Processor, Stream, ct);
                    return new { Outcome = "Success", ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                        Revision = checkpoint?.SourceRevision.Position.Value, ErrorType = (string?)null };
                }
                catch (Exception error)
                {
                    return new { Outcome = "Failed", ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                        Revision = (ulong?)null, ErrorType = error.GetType().FullName };
                }
            }
            var reportingTask = Reporting();
            int count;
            try { count = await cycleTask; }
            finally
            {
                var cycleMs = clock.Elapsed.TotalMilliseconds;
                var reporting = await reportingTask;
                await Record(new { Phase = "Advance534To537", CycleMilliseconds = cycleMs,
                    ReportingRequestStartMilliseconds = requestStartMs, StartedWhileCycleActive = startedWhileCycleActive,
                    PendingHistoricalAtRequest = pendingHistoricalAtRequest, Reporting = reporting,
                    RuntimeHistorical = Summary(counter), ReportingHistorical = Summary(reportingCounter),
                    ExclusiveOwnershipMeasured = false, SharedGateWaitMeasured = false });
            }
            Assert.True(count > 0);
            var at537 = await store.ReadCheckpointAsync(Processor, Stream, ct);
            Assert.NotNull(at537); Assert.Equal(537UL, at537.SourceRevision.Position.Value);
            ValidateCounter(counter);
            // All runtime verification/publisher transactions must load each exact cut at most once.
            Assert.All(counter.Executions.GroupBy(e => (e.Transaction, e.Processor, e.Stream, e.Position)), g => Assert.Single(g));
            Assert.Equal(0, reportingCounter.PendingCount); Assert.Null(reportingCounter.ListenerError);
            // Reader success and overlap coverage are recorded independently from workload correctness.
            // Do not infer that a request overlapped Exclusive ownership from cycle overlap alone.
            _ = await Witness(store, ct);
            var archivedWitnesses537 = await ArchivedWitnesses(connection, ct);
            Assert.All(archivedWitnesses534, archived => Assert.Contains(archived, archivedWitnesses537));

            var projections = new List<OperationalMetricProjection>();
            foreach (var key in at537.BatchManifest.ProjectionKeys)
            {
                var projection = await store.ReadProjectionAsync(Processor, key, ct);
                Assert.NotNull(projection); projections.Add(projection);
            }
            var snapshot = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null);
            using var replayCounter = new ContainmentHistoricalSqlCounter(connectionString);
            var replayClock = Stopwatch.StartNew();
            await new SqlServerOperationalMetricContainmentPublisher(replayCounter.ConnectionString)
                .PublishAsync(at537, at537, projections, ct);
            await Record(new { Phase = "ExactPublisherReplay537", TotalMilliseconds = replayClock.Elapsed.TotalMilliseconds,
                Historical = Summary(replayCounter) });
            ValidateCounter(replayCounter);
            Assert.Equal(snapshot, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null));
            Assert.Equal(524m, await Legacy(connection, ct));
            Assert.Equal(before, await Gg003BoundedContainmentCatchupAcceptanceTests.Fingerprint(connection));
            await Record(new { Phase = "AcceptedCorrectness", Containment = 537, Legacy = 524,
                ProjectionCount = count, WitnessPreserved = true, ReplayStateUnchanged = true, SourceAndLegacyPreserved = true,
                PerformanceAccepted = false, ReportingOverlapCoverage = startedWhileCycleActive && pendingHistoricalAtRequest });
        }
        catch (Exception error)
        {
            await Record(new { Phase = "Failed", ErrorType = error.GetType().FullName,
                RequiresManualCheckpointReconciliation = true, AutomaticRetryAllowed = false });
            throw;
        }
    }

    private static async Task<string[]> ArchivedWitnesses(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @Snapshot nvarchar(max) = (SELECT * FROM dbo.OperationalMetricConflictWitnessArchive ORDER BY WitnessId FOR JSON PATH,INCLUDE_NULL_VALUES); SELECT @Snapshot;";
        var json = (string)(await command.ExecuteScalarAsync(ct))!;
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.EnumerateArray().Select(e => e.GetRawText()).ToArray();
        Assert.NotEmpty(rows);
        return rows;
    }

    private static object Summary(ContainmentHistoricalSqlCounter counter)
    {
        var transactions = new List<SqlTransaction?>();
        return new { counter.PendingCount, ListenerErrorType = counter.ListenerError?.GetType().FullName,
            Executions = counter.Executions.Select(e =>
            {
                var transaction = transactions.FindIndex(t => ReferenceEquals(t, e.Transaction));
                if (transaction < 0) { transaction = transactions.Count; transactions.Add(e.Transaction); }
                return new { TransactionOrdinal = transaction + 1, e.Processor, e.Stream, e.Position, e.Failed,
                    ElapsedMilliseconds = e.Elapsed?.TotalMilliseconds };
            }).ToArray() };
    }

    private static void ValidateCounter(ContainmentHistoricalSqlCounter counter)
    {
        Assert.Null(counter.ListenerError); Assert.Equal(0, counter.PendingCount); Assert.NotEmpty(counter.Executions);
        Assert.All(counter.Executions, e => { Assert.False(e.Failed); Assert.NotNull(e.Transaction); Assert.NotNull(e.Elapsed); });
    }

    private static async Task<decimal> Legacy(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.Position FROM dbo.OperationalMetricProjectionCheckpoint c JOIN dbo.OperationalMetricProjectionProcessor p ON p.OperationalMetricProjectionProcessorRowId=c.OperationalMetricProjectionProcessorRowId WHERE p.ProcessorKey=@Legacy;";
        command.Parameters.AddWithValue("@Legacy", $"operational-metrics:{Machine.Value}:builtins-v1");
        return (decimal)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<AvailabilityScopeConflictEvidenceV1> Witness(SqlServerOperationalMetricProjectionStore store, CancellationToken ct)
    {
        var period = new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(new SiteId("c697c1a0-43b1-4aa7-9233-64344788cdf7"),
            new ShiftScheduleAssignmentId("DEWAS-SR-GG003-SCHEDULE-SHIFT-1"), new ShiftId("SHIFT-1"),
            new DateTimeOffset(2026, 10, 16, 0, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.Zero)));
        OperationalMetricEvaluationKey Key(OperationalMetricDefinitionId id) => new(Machine, period, id, OperationalMetricEvaluationContextKey.Unpartitioned);
        var availability = await store.ReadProjectionAsync(Processor, Key(BuiltInOperationalMetricDefinitions.AvailabilityId), ct);
        Assert.NotNull(availability);
        Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, availability.ReasonCode);
        var witness = Assert.IsType<AvailabilityScopeConflictEvidenceV1>(availability.AvailabilityScopeConflictEvidence);
        var oee = await store.ReadProjectionAsync(Processor, Key(BuiltInOperationalMetricDefinitions.OeeId), ct);
        Assert.NotNull(oee);
        Assert.Equal(witness, oee.DependencyEvidence.Single(d => d.DefinitionId == BuiltInOperationalMetricDefinitions.AvailabilityId).Projection.AvailabilityScopeConflictEvidence);
        Assert.NotNull(availability.PublicationMetadata!.ConflictWitnessId);
        return witness;
    }

    private static OperationalMetricProjectionProcessingRuntime Runtime(string connectionString, ulong ceiling)
    {
        var aggregation = new SqlServerMetricAggregationStore(connectionString);
        var reader = new CappedReader(aggregation, ceiling);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var source = new CoherentOperationalMetricEvaluationBatchSource(catalog, reader, aggregation, SourceProcessor, Stream,
            OperationalMetricEvaluationContextKey.Unpartitioned, useAvailabilityContainmentV1: true);
        return new(Processor, SourceProcessor, Stream, source, new OperationalMetricProjectionFactory(catalog, Processor),
            new SqlServerOperationalMetricProjectionStore(connectionString), new Prerequisite(reader, connectionString),
            new SqlServerOperationalMetricContainmentPublisher(connectionString));
    }

    private sealed class CappedReader(IMetricAggregationRevisionReader reader, ulong ceiling) : IMetricAggregationRevisionReader
    {
        public async ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(MetricAggregationProcessorId processorId, MetricInputStreamId streamId,
            MetricAggregationCheckpoint? afterRevision, CancellationToken cancellationToken)
        {
            var next = await reader.ReadNextAsync(processorId, streamId, afterRevision, cancellationToken);
            return next?.Revision.Position.Value > ceiling ? null : next;
        }
        public ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(MetricAggregationCheckpoint revision, CancellationToken cancellationToken) =>
            revision.Position.Value > ceiling ? ValueTask.FromResult<MetricAggregationRevisionChange?>(null) : reader.ReadExactAsync(revision, cancellationToken);
    }

    private sealed class Prerequisite(CappedReader reader, string connectionString) : IOperationalMetricProjectionPrerequisite
    {
        public async ValueTask<MetricAggregationCheckpoint?> PrepareAsync(OperationalMetricEvaluationBatchRequest request, CancellationToken cancellationToken)
        {
            var next = await reader.ReadNextAsync(request.SourceProcessorId, request.SourceStreamId, request.KnownRevision, cancellationToken);
            if (next is null) return null;
            await new SqlServerProductionReferenceTimeConvergenceCoordinator(connectionString)
                .ConvergeAsync(next.Revision, request.SourceStreamId.MachineId, request.KnownRevision?.Position, cancellationToken);
            var completion = await new SqlServerProductionReferenceTimePublicationTransitionStore(connectionString)
                .ReadAsync(next.Revision.ProcessorId, next.Revision.Position, cancellationToken);
            if (completion is not { IsCompleted: true, CompletedReferenceTimeRevision: not null })
                throw new InvalidDataException("Historical reference-time transition did not complete.");
            return next.Revision;
        }
    }

    public sealed class PerformanceFactAttribute : FactAttribute
    {
        public PerformanceFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)) ||
                Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_PERF_CONFIRM") != "BOOTSTRAP534_ADVANCE537")
                Skip = "Requires explicit opt-in to the exact fresh developer performance clone.";
        }
    }
}

public sealed class Gg003PerformanceGuardTests
{
    [Theory]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true")]
    [InlineData("Server=production;Database=FactoryConnect_GG003_STAGEB_REPLAY_PR152_PERF_20261024;Integrated Security=true")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=FactoryConnect_GG003_STAGEB_REPLAY_PRESERVED537;Integrated Security=true")]
    public void RejectsOtherTargetsBeforeOpeningSql(string connectionString) =>
        Assert.Throws<InvalidOperationException>(() => Gg003RealDataPerformanceTests.RequireTarget(connectionString));

    [Fact]
    public void AcceptsOnlyExplicitDeveloperTarget() =>
        Gg003RealDataPerformanceTests.RequireTarget("Server=(localdb)\\MSSQLLocalDB;Database=" +
            Gg003RealDataPerformanceTests.Database + ";Integrated Security=true");
}
