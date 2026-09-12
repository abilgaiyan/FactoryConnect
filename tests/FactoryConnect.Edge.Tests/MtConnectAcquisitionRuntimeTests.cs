using System.Net;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectAcquisitionRuntimeTests
{
    [Fact]
    public async Task RunCycleAsyncAcquiresAndPublishesBatch()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var sink = new RecordingSink();
        var runtime = CreateRuntime(httpClient, sink, 101);

        var result = await runtime.RunCycleAsync();

        Assert.Same(result, Assert.Single(sink.Results));
        Assert.Equal(111UL, result.NextSequence);
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            Assert.Single(handler.RequestUris).AbsoluteUri);
    }

    [Fact]
    public async Task RunAsyncContinuesFromSessionCursor()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111),
            SampleResponse(42, 120, 121));

        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();

        var sink = new RecordingSink(
            onWrite: () =>
            {
                if (handler.RequestUris.Count == 2)
                {
                    cancellation.Cancel();
                }
            });

        var runtime = CreateRuntime(httpClient, sink, 101);

        await runtime.RunAsync(cancellation.Token);

        Assert.Equal(2, sink.Results.Count);
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            handler.RequestUris[0].AbsoluteUri);
        Assert.Equal(
            "http://localhost:5000/sample?from=111",
            handler.RequestUris[1].AbsoluteUri);
    }

    [Fact]
    public async Task RunAsyncRetainsExactPendingWriteAfterSinkFailure()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var contactTime = new DateTimeOffset(
            2026,
            9,
            11,
            7,
            0,
            0,
            TimeSpan.Zero);
        var timeProvider = new CountingTimeProvider(contactTime);
        var sink = new FailOnceSink(
            onSuccessfulRetry: cancellation.Cancel);
        var runtime = CreateRuntime(
            httpClient,
            sink,
            101,
            timeProvider: timeProvider);

        await runtime.RunAsync(cancellation.Token);

        Assert.Single(handler.RequestUris);
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            handler.RequestUris[0].AbsoluteUri);
        Assert.Equal(1, timeProvider.ReadCount);
        Assert.Equal(2, sink.WriteCount);
        Assert.Equal(2, sink.Results.Count);
        Assert.Same(sink.Results[0], sink.Results[1]);
        Assert.Equal(2, sink.ExpectedCheckpoints.Count);
        Assert.Null(sink.ExpectedCheckpoints[0]);
        Assert.Null(sink.ExpectedCheckpoints[1]);
        Assert.Equal([contactTime, contactTime], sink.SuccessfulContactTimes);
    }

    [Fact]
    public async Task RunAsyncStopsWhenPollingDelayIsCancelled()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();

        var sink = new RecordingSink(
            onWrite: cancellation.Cancel);

        var runtime = CreateRuntime(httpClient, sink, 101);

        await runtime.RunAsync(cancellation.Token);

        Assert.Single(sink.Results);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task RunCycleAsyncDoesNotPublishFailedAcquisition()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable));

        using var httpClient = new HttpClient(handler);
        var sink = new RecordingSink();
        var timeProvider = new CountingTimeProvider(DateTimeOffset.UnixEpoch);
        var runtime = CreateRuntime(
            httpClient,
            sink,
            101,
            timeProvider: timeProvider);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => runtime.RunCycleAsync());

        Assert.Empty(sink.Results);
        Assert.Equal(0, timeProvider.ReadCount);
    }

    [Fact]
    public async Task RunCycleAsyncRetriesSameCursorBeforePublishing()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable),
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var sink = new RecordingSink();
        var runtime = CreateRuntime(
            httpClient,
            sink,
            101,
            maxAttempts: 2);

        await runtime.RunCycleAsync();

        Assert.Equal(2, handler.RequestUris.Count);
        Assert.All(
            handler.RequestUris,
            uri => Assert.Equal(
                "http://localhost:5000/sample?from=101",
                uri.AbsoluteUri));
        Assert.Single(sink.Results);
    }

    [Fact]
    public async Task RunCycleAsyncDoesNotRetrySinkFailureWithinSingleCall()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var runtime = CreateRuntime(
            httpClient,
            new FailingSink(),
            101,
            maxAttempts: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task RunCycleAsyncRetriesExactPendingResultAfterSinkFailure()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var sink = new FailOnceSink();
        var runtime = CreateRuntime(httpClient, sink, 101);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        await runtime.RunCycleAsync();

        Assert.Single(handler.RequestUris);
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            handler.RequestUris[0].AbsoluteUri);
        Assert.Equal(2, sink.WriteCount);
        Assert.Same(sink.Results[0], sink.Results[1]);
        Assert.Equal(sink.ExpectedCheckpoints[0], sink.ExpectedCheckpoints[1]);
        Assert.Equal(
            sink.SuccessfulContactTimes[0],
            sink.SuccessfulContactTimes[1]);
    }

    [Fact]
    public async Task RunCycleAsyncRetriesExactPendingResultAfterStoreFailure()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 110, 111));

        using var httpClient = new HttpClient(handler);
        var machineId = MachineId.New();
        var store = new FailOnceObservationIngestionStore();
        var streamId = MtConnectObservationStreamId.Create(
            machineId,
            "CNC-01");
        var sink = new MtConnectDurableObservationSink(
            store,
            streamId);
        var runtime = CreateRuntime(
            httpClient,
            sink,
            101,
            machineId: machineId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        await runtime.RunCycleAsync();

        Assert.Single(handler.RequestUris);
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            handler.RequestUris[0].AbsoluteUri);

        var checkpoint =
            await store.Inner.ReadCheckpointAsync(streamId);

        Assert.Equal(42UL, checkpoint?.InstanceId);
        Assert.Equal(111UL, checkpoint?.NextSequence);
        Assert.Single(store.Inner.ReadObservations(streamId));
    }

    [Fact]
    public async Task RunCycleAsyncPropagatesAcquisitionCancellation()
    {
        using var httpClient = new HttpClient(
            new CancellingHandler());

        var runtime = CreateRuntime(
            httpClient,
            new RecordingSink(),
            101);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RunCycleAsync(cancellation.Token));
    }

    [Fact]
    public void ConstructorRejectsNonPositivePollingInterval()
    {
        using var httpClient = new HttpClient(
            new SequenceHandler());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateRuntime(
                httpClient,
                new RecordingSink(),
                101,
                TimeSpan.Zero));
    }

    private static MtConnectAcquisitionRuntime CreateRuntime(
        HttpClient httpClient,
        IMtConnectObservationSink sink,
        ulong fromSequence,
        TimeSpan? pollingInterval = null,
        int maxAttempts = 1,
        MachineId? machineId = null,
        TimeProvider? timeProvider = null)
    {
        var runtimeMachineId = machineId ?? MachineId.New();

        return new MtConnectAcquisitionRuntime(
            new MtConnectAcquisitionSession(
                new MtConnectSampleClient(httpClient),
                fromSequence),
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            runtimeMachineId,
            "CNC-01",
            new MtConnectTransientRetryPolicy(
                new MtConnectRetryOptions(
                    maxAttempts,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1),
                    0),
                new ImmediateRetryDelay(),
                new FixedJitterSource(),
                NullLogger<MtConnectTransientRetryPolicy>.Instance),
            new MtConnectContinuityRecoveryPolicy(
                new MtConnectAcquisitionSessionFactory(
                    new MtConnectSampleClient(httpClient)),
                new MtConnectCurrentClient(httpClient),
                new MtConnectTransientRetryPolicy(
                    new MtConnectRetryOptions(
                        1,
                        TimeSpan.FromMilliseconds(1),
                        TimeSpan.FromMilliseconds(1),
                        0),
                    new ImmediateRetryDelay(),
                    new FixedJitterSource(),
                    NullLogger<MtConnectTransientRetryPolicy>.Instance),
                new IgnoringContinuityReporter()),
            sink,
            timeProvider ?? TimeProvider.System,
            pollingInterval ?? TimeSpan.FromMilliseconds(1));
    }

    private static HttpResponseMessage SampleResponse(
        ulong instanceId,
        ulong lastSequence,
        ulong nextSequence)
    {
        var xml = $"""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="{instanceId}"
                      firstSequence="1"
                      lastSequence="{lastSequence}"
                      nextSequence="{nextSequence}" />
              <Streams>
                <DeviceStream name="CNC-01" uuid="uuid-1">
                  <ComponentStream component="Controller" componentId="c1">
                    <Events>
                      <Execution dataItemId="exec"
                                 timestamp="2026-08-23T10:00:00Z"
                                 sequence="{lastSequence}">ACTIVE</Execution>
                    </Events>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        };
    }

    private sealed class RecordingSink(
        Action? onWrite = null) : IMtConnectObservationSink
    {
        public List<MtConnectSampleResult> Results { get; } = [];

        public ValueTask WriteAsync(
            MtConnectSampleResult result,
            ObservationCheckpoint? expectedCheckpoint,
            DateTimeOffset successfulContactTime,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Results.Add(result);
            onWrite?.Invoke();

            return ValueTask.CompletedTask;
        }
    }

    private sealed class IgnoringContinuityReporter :
        IMtConnectContinuityReporter
    {
        public ValueTask ReportAsync(
            MtConnectContinuityLoss continuityLoss,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateRetryDelay :
        IMtConnectRetryDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    private sealed class FixedJitterSource :
        IMtConnectJitterSource
    {
        public double NextDouble() => 0.5;
    }

    private sealed class FailingSink :
        IMtConnectObservationSink
    {
        public ValueTask WriteAsync(
            MtConnectSampleResult result,
            ObservationCheckpoint? expectedCheckpoint,
            DateTimeOffset successfulContactTime,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Sink failed.");
        }
    }

    private sealed class FailOnceSink(
        Action? onSuccessfulRetry = null) : IMtConnectObservationSink
    {
        public int WriteCount { get; private set; }

        public List<MtConnectSampleResult> Results { get; } = [];

        public List<ObservationCheckpoint?> ExpectedCheckpoints { get; } = [];

        public List<DateTimeOffset> SuccessfulContactTimes { get; } = [];

        public ValueTask WriteAsync(
            MtConnectSampleResult result,
            ObservationCheckpoint? expectedCheckpoint,
            DateTimeOffset successfulContactTime,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            Results.Add(result);
            ExpectedCheckpoints.Add(expectedCheckpoint);
            SuccessfulContactTimes.Add(successfulContactTime);

            if (WriteCount == 1)
            {
                throw new InvalidOperationException(
                    "Sink failed.");
            }

            onSuccessfulRetry?.Invoke();

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceObservationIngestionStore :
        IObservationIngestionStore
    {
        private bool _failNextCommit = true;

        public InMemoryObservationIngestionStore Inner { get; } = new();

        public ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default)
        {
            return Inner.ReadCheckpointAsync(
                streamId,
                cancellationToken);
        }

        public ValueTask<AcquisitionContactAuthority?>
            ReadAcquisitionContactAuthorityAsync(
                ObservationStreamId streamId,
                CancellationToken cancellationToken = default)
        {
            return Inner.ReadAcquisitionContactAuthorityAsync(
                streamId,
                cancellationToken);
        }

        public ValueTask CommitAsync(
            ObservationIngestionBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_failNextCommit)
            {
                _failNextCommit = false;

                throw new InvalidOperationException(
                    "Store commit failed.");
            }

            return Inner.CommitAsync(
                batch,
                cancellationToken);
        }
    }

    private sealed class CountingTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return utcNow;
        }
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class SequenceHandler(
        params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses =
            new(responses);

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                RequestUris.Add(request.RequestUri);
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No HTTP response configured for request.");
            }

            var response = _responses.Dequeue();
            response.RequestMessage = request;

            return Task.FromResult(response);
        }
    }
}
