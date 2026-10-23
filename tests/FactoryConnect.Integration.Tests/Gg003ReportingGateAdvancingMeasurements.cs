using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003ReportingGateAdvancingMeasurements(ITestOutputHelper output)
{
    private const string Variable = "FACTORYCONNECT_GG003_GATE_ADVANCE_CONNECTION_STRING";
    private const string DateVariable = "FACTORYCONNECT_GG003_GATE_RCA_DATE";
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private readonly ConcurrentQueue<string> _phases = new();
    private readonly ConcurrentDictionary<Guid, string> _activePhases = new();

    [AdvanceRestoreFact]
    public async Task ActualRuntimeAdvancesOneCompleteRevisionWithConcurrentReportingAndPhaseMeasurements()
    {
        var settings = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)!);
        Assert.True(OperatingSystem.IsWindows());
        Assert.StartsWith("(localdb)\\", settings.DataSource, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("FactoryConnect_GG003_GATE_ADVANCE_", settings.InitialCatalog, StringComparison.Ordinal);
        var date = DateOnly.ParseExact(Environment.GetEnvironmentVariable(DateVariable)!, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var token = deadline.Token;
        var application = "FC-Gate-Advance-" + Guid.NewGuid().ToString("N");
        string Role(string role) => ReportingGateCommandTrace.Connection(settings.ConnectionString, application, role);
        using var trace = new ReportingGateCommandTrace(application);
        var sampler = new ReportingGateWaitSampler(settings.ConnectionString, application,
            () => _activePhases.IsEmpty ? "outside-named-phase" :
                string.Join(",", _activePhases.Values.Distinct().Order(StringComparer.Ordinal)));
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(token);
        var samplingTask = sampler.RunAsync(sampling.Token);
        Exception? workloadFailure = null;
        var workloadSucceeded = false;
        try
        {
            await sampler.Ready.Task.WaitAsync(token);
            await using var inspect = new SqlConnection(settings.ConnectionString);
            await inspect.OpenAsync(token);
            await using (var guard = inspect.CreateCommand())
            {
                guard.CommandText = "SELECT DB_NAME();";
                Assert.Equal(settings.InitialCatalog, (string)(await guard.ExecuteScalarAsync(token))!);
            }
            var processor = AvailabilityContainmentV1.ProjectionProcessorId(Machine);
            var stream = MetricInputStreamId.ForMachine(Machine);
            var aggregationProcessor = new MetricAggregationProcessorId($"metric-aggregation:{Machine.Value}");
            var aggregation = new SqlServerMetricAggregationStore(Role("source"));
            var store = new SqlServerOperationalMetricProjectionStore(Role("checkpoint"));
            output.WriteLine("processor=" + processor.Value);
            output.WriteLine("gate=" + SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(processor));
            output.WriteLine("stopwatchFrequency=" + Stopwatch.Frequency);
            var before = await Measure("initial-checkpoint-read", () =>
                new SqlServerOperationalMetricProjectionStore(Role("initial-checkpoint"))
                    .ReadCheckpointAsync(processor, stream, token).AsTask());
            Assert.NotNull(before);
            // This investigation starts from a fresh copy of the accepted revision-534 evidence.
            Assert.Equal(534UL, before.SourceRevision.Position.Value);
            var next = await Measure("next-revision-discovery", () =>
                aggregation.ReadNextAsync(aggregationProcessor, stream, before.SourceRevision, token).AsTask());
            Assert.NotNull(next);
            Assert.True(next.Revision.Position > before.SourceRevision.Position);
            Assert.NotNull(await aggregation.ReadExactAsync(next.Revision, token));
            output.WriteLine("startCheckpoint=" + before.SourceRevision.Position.Value);
            output.WriteLine("nextRevision=" + next.Revision.Position.Value);
            output.WriteLine("requestedDate=" + date);
            var immutableBefore = await Gg003RestoredContainmentRuntimeAcceptanceTests.Fingerprint(inspect);
            var revisionsBefore = await RevisionLedger(inspect, token);
            var baseline = await Measure("baseline-whole-publication", () =>
                new SqlServerOperationalMetricProjectionSummaryReader(Role("baseline"))
                    .ReadCurrentPublicationSummariesAsync(processor, token).AsTask());
            var requested = baseline.Where(s => s.Key.MachineId == Machine &&
                s.Key.PeriodId is OperationalMetricPeriodId.ProductionDay day &&
                day.ProductionDayId.BusinessDate == date &&
                s.Key.ContextKey == OperationalMetricEvaluationContextKey.Unpartitioned).ToArray();
            Assert.NotEmpty(requested);
            var period = requested[0].Key.PeriodId;
    
            var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
            var source = new CoherentOperationalMetricEvaluationBatchSource(catalog, aggregation, aggregation,
                aggregationProcessor, stream, OperationalMetricEvaluationContextKey.Unpartitioned,
                useAvailabilityContainmentV1: true);
            var publisher = new MeasuredPublisher(this, Role("publisher"), before, next.Revision);
            var runtime = new OperationalMetricProjectionProcessingRuntime(processor, aggregationProcessor, stream,
                new MeasuredSource(this, source), new OperationalMetricProjectionFactory(catalog, processor), store,
                new NextPrerequisite(this, Role("prerequisite"), next.Revision), publisher);
            Task<int>? advance = null;
            IReadOnlyList<OperationalMetricProjectionSummary>? concurrent = null;
            try
            {
                var gateTask = trace.ExclusiveGateAsync("publisher");
                advance = Task.Run(() => Measure("complete-runtime-cycle", () => runtime.RunCycleAsync(token).AsTask()), token);
                // Do not wait 30 minutes for a gate if batch construction fails first.
                await Task.WhenAny(gateTask, advance).WaitAsync(token);
                if (!gateTask.IsCompleted)
                {
                    await advance;
                    throw new InvalidDataException("Runtime returned without an observed exclusive publication gate.");
                }
                var gate = await gateTask;
                Assert.False(gate.Failed);
                try
                {
                    concurrent = await Measure("concurrent-period-read", () =>
                        new SqlServerOperationalMetricProjectionSummaryReader(Role("report"))
                            .ReadPeriodSummariesAsync(processor, Machine, period,
                                OperationalMetricEvaluationContextKey.Unpartitioned, token).AsTask());
                    output.WriteLine("concurrentRead=success");
                }
                catch (TimeoutException error) when (error.Message == "Processor gate acquisition timed out.")
                {
                    output.WriteLine("concurrentRead=processor-gate-timeout");
                }
                Assert.True(await advance > 0);
            }
            finally
            {
                if (advance is not null) await advance;
            }
            var proposed = publisher.Proposed;
            Assert.NotNull(proposed);
            Assert.Equal(next.Revision, proposed.SourceRevision);
            Assert.Equal(proposed, await store.ReadCheckpointAsync(processor, stream, token));
            var after = await Measure("post-advance-whole-publication", () =>
                new SqlServerOperationalMetricProjectionSummaryReader(Role("after"))
                    .ReadCurrentPublicationSummariesAsync(processor, token).AsTask());
            foreach (var projection in publisher.Projections)
            {
                var summary = Assert.Single(after, s => s.Key == projection.Key);
                Assert.Equal(projection.SourceRevision, summary.SourceRevision);
                Assert.Equal(projection.Status, summary.Status);
                Assert.Equal(projection.Value, summary.Value);
                Assert.Equal(projection.Unit, summary.Unit);
                Assert.Equal(projection.ReasonCode, summary.ReasonCode);
                Assert.Equal(projection.ReasonOperandName, summary.ReasonOperandName);
            }
            if (concurrent is not null)
            {
                var expected = after.Where(s => s.Key.MachineId == Machine && s.Key.PeriodId == period &&
                    s.Key.ContextKey == OperationalMetricEvaluationContextKey.Unpartitioned);
                Assert.Equal(expected.Select(Identity).Order(StringComparer.Ordinal),
                    concurrent.Select(Identity).Order(StringComparer.Ordinal));
            }
            Assert.Equal(immutableBefore, await Gg003RestoredContainmentRuntimeAcceptanceTests.Fingerprint(inspect));
            Assert.Equal(revisionsBefore, await RevisionLedger(inspect, token));
    
            // The real publisher is used above, without callback substitution or invented envelopes.
            // These are independent read-only probes of each retained envelope, not inferred subtractions.
            await VerifyRetainedPhases(settings.ConnectionString, application, processor, token);
            var exclusive = trace.Entries.Where(e => e.Role == "publisher" && e.Kind == "gate" &&
                e.LockMode == "Exclusive").OrderBy(e => e.StartedAt).First();
            Assert.False(string.IsNullOrWhiteSpace(exclusive.TransactionId));
            var commit = Assert.Single(trace.Entries, e => e.Role == "publisher" &&
                e.Kind == "transaction-commit" && e.TransactionId == exclusive.TransactionId);
            Assert.False(commit.Failed);
            output.WriteLine("exclusiveGateToCommitReturnUpperBoundMs=" +
                Stopwatch.GetElapsedTime(exclusive.CompletedAt, commit.CompletedAt).TotalMilliseconds);
            output.WriteLine("publicationTransactionCommitMs=" + commit.Milliseconds);
            output.WriteLine("finalCheckpoint=" + proposed.SourceRevision.Position.Value);
            output.WriteLine("One complete next runtime batch committed. No synthetic batch, direct checkpoint manipulation, migration or source advancement.");
            workloadSucceeded = true;
        }
        catch (Exception error)
        {
            workloadFailure = error;
            throw;
        }
        finally
        {
            await sampling.CancelAsync();
            try { await samplingTask; }
            catch (Exception monitorError) when (workloadFailure is not null)
            {
                output.WriteLine(JsonSerializer.Serialize(new { measurement = "secondary-monitor-failure",
                    exceptionType = monitorError.GetType().FullName,
                    primaryExceptionType = workloadFailure.GetType().FullName }));
                // Keep the original workload failure; the monitor journal retains its independent failure.
            }
            finally
            {
                output.WriteLine(JsonSerializer.Serialize(new { measurement = "diagnostic-acceptance",
                    workloadSucceeded, samplerFailed = sampler.FailureRecord is not null,
                    monitorTaskSucceeded = samplingTask.IsCompletedSuccessfully, journal = sampler.JournalPath }));
                sampler.Write(output);
                foreach (var phase in _phases) output.WriteLine(phase);
                trace.Write(output);
            }
        }
    }

    private async Task VerifyRetainedPhases(string connectionString, string application,
        OperationalMetricProjectionProcessorId processor, CancellationToken token)
    {
        await using var connection = new SqlConnection(ReportingGateCommandTrace.Connection(connectionString, application, "phases"));
        await connection.OpenAsync(token);
        await using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, tx, processor,
            OperationalMetricProjectionProcessorGateMode.Shared, token);
        var payloads = await Measure("retained-envelope-read", async () =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                SELECT a.ResultPayload
                FROM dbo.OperationalMetricProjection r
                JOIN dbo.OperationalMetricProjectionProcessor p
                  ON p.OperationalMetricProjectionProcessorRowId=r.OperationalMetricProjectionProcessorRowId
                JOIN dbo.OperationalMetricPublicationResultArchive a ON a.ResultId=r.ContainmentResultId
                WHERE p.ProcessorKeyBinary=@Key
                ORDER BY r.OperationalMetricProjectionRowId;
                """;
            command.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(processor.Value);
            var result = new List<ReadOnlyMemory<byte>>();
            await using var rows = await command.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token)) result.Add((byte[])rows[0]);
            return result;
        });
        Assert.NotEmpty(payloads);
        for (var i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];
            var batch = ContainmentArchiveBatch.Preflight([payload]);
            await Measure("archive-prepare-verify-only-result-" + i, async () =>
            {
                _ = await SqlContainmentArchiveTransaction.PrepareAsync(connection, tx, [payload], true, token, verifyOnly: true);
                return true;
            });
            var reconstructed = await Measure("historical-reconstruction-result-" + i, () =>
                SqlContainmentHistoricalResultValidator.ReconstructVerifiedAsync(connection, tx,
                    batch.Results[0].Document.Key, batch.Cut, token));
            Assert.Equal(payload.ToArray(), OperationalMetricDependencySnapshotV2Codec.Encode(reconstructed.Document));
        }
        await Measure("standalone-verification-transaction-commit", async () => { await tx.CommitAsync(token); return true; });
    }

    private async Task<T> Measure<T>(string operation, Func<Task<T>> run)
    {
        var started = Stopwatch.GetTimestamp();
        var phaseId = Guid.NewGuid();
        _activePhases[phaseId] = operation;
        var succeeded = false;
        try { var result = await run(); succeeded = true; return result; }
        finally
        {
            var ended = Stopwatch.GetTimestamp();
            _activePhases.TryRemove(phaseId, out _);
            _phases.Enqueue(JsonSerializer.Serialize(new
            {
                measurement = "phase", operation, startedAt = started, completedAt = ended,
                milliseconds = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds, succeeded,
            }));
        }
    }

    private static string Identity(OperationalMetricProjectionSummary s) =>
        Convert.ToHexString(OperationalMetricEvaluationKeyV1Codec.Encode(s.Key)) + "|" +
        s.SourceRevision.Position.Value + "|" + s.Status + "|" +
        s.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + s.Unit + "|" +
        s.ReasonCode + "|" + s.ReasonOperandName;

    private static async Task<string> RevisionLedger(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
            (SELECT * FROM dbo.MetricAggregationRevision ORDER BY MetricAggregationProcessorRowId,Position FOR JSON PATH,INCLUDE_NULL_VALUES) AS Revisions,
            (SELECT * FROM dbo.MetricAggregationRevisionShiftOccurrence ORDER BY MetricAggregationProcessorRowId,Position,ShiftOccurrenceIdentityHash FOR JSON PATH,INCLUDE_NULL_VALUES) AS Shifts,
            (SELECT * FROM dbo.MetricAggregationRevisionProductionDay ORDER BY MetricAggregationProcessorRowId,Position,SiteOrderKey,ProductionBusinessDate FOR JSON PATH,INCLUDE_NULL_VALUES) AS Days
            FOR JSON PATH;
            """;
        var json = (string)(await command.ExecuteScalarAsync(token))!;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private sealed class MeasuredSource(Gg003ReportingGateAdvancingMeasurements owner,
        IOperationalMetricEvaluationBatchSource inner) : IOperationalMetricEvaluationBatchSource
    {
        public async ValueTask<OperationalMetricEvaluationBatch?> ReadAsync(OperationalMetricEvaluationBatchRequest request,
            CancellationToken token) => await owner.Measure("complete-next-batch-evaluation", () => inner.ReadAsync(request, token).AsTask());
    }

    private sealed class MeasuredPublisher(Gg003ReportingGateAdvancingMeasurements owner, string connectionString,
        OperationalMetricProjectionCheckpoint expected, MetricAggregationCheckpoint next) : IOperationalMetricContainmentPublisher
    {
        internal OperationalMetricProjectionCheckpoint? Proposed { get; private set; }
        internal IReadOnlyList<OperationalMetricProjection> Projections { get; private set; } = [];
        public async ValueTask PublishAsync(OperationalMetricProjectionCheckpoint? expectedCheckpoint,
            OperationalMetricProjectionCheckpoint proposedCheckpoint, IReadOnlyList<OperationalMetricProjection> projections,
            CancellationToken token)
        {
            Assert.Null(Proposed);
            Assert.Equal(expected, expectedCheckpoint);
            Assert.Equal(next, proposedCheckpoint.SourceRevision);
            Assert.NotEmpty(projections);
            Proposed = proposedCheckpoint;
            Projections = projections.ToArray();
            await owner.Measure("actual-containment-publisher", async () =>
            {
                await new SqlServerOperationalMetricContainmentPublisher(connectionString)
                    .PublishAsync(expectedCheckpoint, proposedCheckpoint, projections, token);
                return true;
            });
        }
    }

    private sealed class NextPrerequisite(Gg003ReportingGateAdvancingMeasurements owner, string connectionString,
        MetricAggregationCheckpoint next) : IOperationalMetricProjectionPrerequisite
    {
        public async ValueTask<MetricAggregationCheckpoint?> PrepareAsync(OperationalMetricEvaluationBatchRequest request,
            CancellationToken token) => await owner.Measure<MetricAggregationCheckpoint?>("reference-time-prerequisite", async () =>
        {
            Assert.NotNull(request.KnownRevision);
            Assert.True(next.Position > request.KnownRevision.Position);
            await new SqlServerProductionReferenceTimeConvergenceCoordinator(connectionString)
                .ConvergeAsync(next, request.SourceStreamId.MachineId, request.KnownRevision.Position, token);
            var completion = await new SqlServerProductionReferenceTimePublicationTransitionStore(connectionString)
                .ReadAsync(next.ProcessorId, next.Position, token);
            Assert.True(completion is { IsCompleted: true, CompletedReferenceTimeRevision: not null });
            return next;
        });
    }

    public sealed class AdvanceRestoreFactAttribute : FactAttribute
    {
        public AdvanceRestoreFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || new[] { Variable, DateVariable }
                .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Requires a fresh separate GG003 LocalDB GATE_ADVANCE clone at revision 534 and an explicit business date.";
        }
    }
}
