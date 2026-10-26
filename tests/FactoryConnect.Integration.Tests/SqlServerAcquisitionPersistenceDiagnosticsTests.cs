using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionPersistenceDiagnosticsTests(SqlContainmentAuthorityDatabaseFixture fixture, ITestOutputHelper output)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Fact]
    public async Task LongHistoryComparisonReportsExactReadAndAppendCommandsAndTransactionDurations()
    {
        var seedStore = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var history = await AcquisitionHistoryWorkload.SeedAsync(seedStore, 3, 257);
        using var capture = new AcquisitionPersistenceCapture(fixture.ConnectionString);
        var measuredStore = new SqlServerObservationIngestionStore(capture.ConnectionString);
        var latest = history[^1];
        var identity = Convert.ToHexString(AcquisitionEvidenceCodec.Identity(latest, AcquisitionEvidenceCodec.Encode(latest)));
        var timer = Stopwatch.StartNew();
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await measuredStore.ReadExactAsync(latest.Checkpoint.StreamId, identity)).Status);
        output.WriteLine("Exact historical read: {0:F3} ms, {1} source SQL commands", timer.Elapsed.TotalMilliseconds, capture.SourceCommands);
        var append = AcquisitionHistoryWorkload.Batch(latest.Checkpoint.StreamId, latest.Checkpoint, 257);
        var beforeCommands = capture.SourceCommands;
        timer.Restart();
        await measuredStore.CommitAsync(append);
        output.WriteLine("Append: {0:F3} ms, {1} historical source SQL commands", timer.Elapsed.TotalMilliseconds, capture.SourceCommands - beforeCommands);
        foreach (var span in capture.Spans.Where(span => span.OperationName is "acquisition.transaction.lifetime" or "acquisition.raw-state.write" or "acquisition.evidence.sources.verify"))
            output.WriteLine("{0}: {1:F3} ms publications={2} observations={3} sqlCommands={4}", span.OperationName,
                span.Duration.TotalMilliseconds, span.GetTagItem("publication.count"), span.GetTagItem("observation.count"), span.GetTagItem("sql.command.count"));
        Assert.Equal(append.Checkpoint, await measuredStore.ReadCheckpointAsync(append.Checkpoint.StreamId));
        Assert.Null(capture.Error);
    }

    [Fact]
    public async Task TimingsAndSourceCommandCountsDescribeTheRealCommitAndExactRead()
    {
        using var capture = new AcquisitionPersistenceCapture(fixture.ConnectionString);
        var store = new SqlServerObservationIngestionStore(capture.ConnectionString);
        var first = AcquisitionEvidenceCodecTests.Batch();
        await store.CommitAsync(first);
        var next = AcquisitionEvidenceCodecTests.Batch(first.Checkpoint.StreamId, expected: first.Checkpoint, requested: 10, next: 20);
        await store.CommitAsync(next);
        var identity = Convert.ToHexString(AcquisitionEvidenceCodec.Identity(next, AcquisitionEvidenceCodec.Encode(next)));
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await store.ReadExactAsync(next.Checkpoint.StreamId, identity)).Status);
        foreach (var stage in new[] { "acquisition.commit", "acquisition.transaction.lifetime", "acquisition.reconcile",
            "acquisition.raw-state.write", "acquisition.evidence.append", "acquisition.evidence.prefix.read-validate",
            "acquisition.evidence.sources.verify", "acquisition.transaction.commit" })
        {
            var spans = capture.Spans.Where(span => span.OperationName == stage).ToArray();
            Assert.NotEmpty(spans);
            Assert.All(spans, span => Assert.Equal(true, span.GetTagItem("stage.completed")));
            foreach (var span in spans)
                output.WriteLine("{0}: {1:F3} ms publications={2} observations={3} sqlCommands={4}", stage,
                    span.Duration.TotalMilliseconds, span.GetTagItem("publication.count"), span.GetTagItem("observation.count"), span.GetTagItem("sql.command.count"));
        }
        var recordedCommands = capture.Spans.Where(span => span.OperationName == "acquisition.evidence.sources.verify")
            .Sum(span => Convert.ToInt64(span.GetTagItem("sql.command.count"), CultureInfo.InvariantCulture));
        Assert.Equal(capture.SourceCommands, recordedCommands);
        Assert.True(recordedCommands > 0);
        Assert.All(capture.SourceTransactions, transaction => Assert.NotNull(transaction));
        Assert.Null(capture.Error);
    }
}

// Counts actual SqlClient command-start events in the source-validation span, not cache calls.
// A unique application name and trace isolate parallel SQL fixtures.
internal sealed class AcquisitionPersistenceCapture : IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly Activity _parent = new Activity("acquisition-persistence-test").SetIdFormat(ActivityIdFormat.W3C).Start();
    private readonly ActivityListener _listener;
    private readonly IDisposable _all;
    private readonly ConcurrentBag<IDisposable> _subscriptions = [];
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentQueue<SqlTransaction?> _transactions = new();
    private readonly ConcurrentQueue<int> _parameterCounts = new();
    private readonly string _application = "FC-AcquisitionMeasure:" + Guid.NewGuid().ToString("N");
    private long _sourceCommands;

    internal AcquisitionPersistenceCapture(string connectionString, Action<Activity>? onStopped = null)
    {
        ConnectionString = new SqlConnectionStringBuilder(connectionString) { ApplicationName = _application }.ConnectionString;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Acquisition",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != _parent.TraceId) return;
                _spans.Enqueue(activity);
                onStopped?.Invoke(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
        _all = DiagnosticListener.AllListeners.Subscribe(this);
    }

    internal string ConnectionString { get; }
    internal Activity[] Spans => _spans.ToArray();
    internal SqlTransaction?[] SourceTransactions => _transactions.ToArray();
    internal int[] SourceParameterCounts => _parameterCounts.ToArray();
    internal long SourceCommands => Interlocked.Read(ref _sourceCommands);
    internal Exception? Error { get; private set; }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "SqlClientDiagnosticListener") _subscriptions.Add(listener.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> item)
    {
        if (!item.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) || Activity.Current?.TraceId != _parent.TraceId) return;
        var sourceValidation = false;
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            if (activity.OperationName == "acquisition.evidence.sources.verify") sourceValidation = true;
        if (!sourceValidation || item.Value?.GetType().GetProperty("Command")?.GetValue(item.Value) is not SqlCommand command ||
            command.Connection is null || new SqlConnectionStringBuilder(command.Connection.ConnectionString).ApplicationName != _application) return;
        Interlocked.Increment(ref _sourceCommands);
        _transactions.Enqueue(command.Transaction);
        _parameterCounts.Enqueue(command.Parameters.Count);
    }

    public void OnError(Exception error) => Error = error;
    public void OnCompleted() { }
    public void Dispose()
    {
        _all.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _listener.Dispose();
        _parent.Dispose();
    }
}

internal static class AcquisitionHistoryWorkload
{
    internal static async Task<ObservationIngestionBatch[]> SeedAsync(SqlServerObservationIngestionStore store, int publications, int observationsPerPublication)
    {
        var stream = new ObservationStreamId(MachineId.New(), "acquisition-batch-workload");
        var batches = new List<ObservationIngestionBatch>();
        ObservationCheckpoint? previous = null;
        for (var index = 0; index < publications; index++)
        {
            var batch = Batch(stream, previous, observationsPerPublication);
            await store.CommitAsync(batch);
            batches.Add(batch);
            previous = batch.Checkpoint;
        }
        return batches.ToArray();
    }

    internal static ObservationIngestionBatch Batch(ObservationStreamId stream, ObservationCheckpoint? previous, int count)
    {
        var first = previous?.NextSequence ?? 1UL;
        var next = checked(first + (ulong)count);
        var prototype = AcquisitionEvidenceCodecTests.Batch(stream).Observations[0].Observation;
        var observations = Enumerable.Range(0, count).Select(index =>
        {
            var sequence = first + (ulong)index;
            return new SequencedMachineObservation(sequence, prototype with { Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence) });
        }).ToArray();
        return new ObservationIngestionBatch(previous, new ObservationCheckpoint(stream, previous?.InstanceId ?? 1, next), observations,
            DateTimeOffset.UnixEpoch.AddSeconds(next), new AcquisitionResponseEvidence(Guid.NewGuid(), first, 1, next, []));
    }
}
