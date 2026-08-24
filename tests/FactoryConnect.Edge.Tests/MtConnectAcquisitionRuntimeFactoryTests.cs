using System.Net;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectAcquisitionRuntimeFactoryTests
{
    [Fact]
    public async Task CreateAsyncUsesConfiguredBootstrapWithoutCheckpoint()
    {
        var handler = new SequenceHandler(
            SampleResponse(instanceId: 42, nextSequence: 111));

        using var httpClient = new HttpClient(handler);
        var store = new InMemoryObservationIngestionStore();
        var sink = new RecordingSink();
        var sessionFactory = new RecordingSessionFactory(
            new MtConnectSampleClient(httpClient));
        var factory = CreateFactory(
            httpClient,
            store,
            sessionFactory,
            sink,
            fromSequence: 101);

        var runtime = Assert.IsType<MtConnectAcquisitionRuntime>(
            await factory.CreateAsync());

        await runtime.RunCycleAsync();

        Assert.Equal(101UL, Assert.Single(sessionFactory.Sequences));
        Assert.Null(Assert.Single(sink.ExpectedCheckpoints));
        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            Assert.Single(handler.RequestUris).AbsoluteUri);
    }

    [Fact]
    public async Task CreateAsyncRestoresSessionAndContinuesSameInstance()
    {
        var handler = new SequenceHandler(
            SampleResponse(instanceId: 42, nextSequence: 511));

        using var httpClient = new HttpClient(handler);
        var machineId = MachineId.New();
        var options = Options(machineId, fromSequence: 101);
        var streamId = MtConnectObservationStreamId.Create(
            machineId,
            options.DeviceKey);
        var checkpoint = new ObservationCheckpoint(
            streamId,
            instanceId: 42,
            nextSequence: 500);
        var store = new InMemoryObservationIngestionStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                []));

        var sink = new RecordingSink();
        var sessionFactory = new RecordingSessionFactory(
            new MtConnectSampleClient(httpClient));
        var factory = CreateFactory(
            httpClient,
            store,
            sessionFactory,
            sink,
            options);

        var runtime = Assert.IsType<MtConnectAcquisitionRuntime>(
            await factory.CreateAsync());

        await runtime.RunCycleAsync();

        Assert.Empty(sessionFactory.Sequences);
        Assert.Equal(
            (42UL, 500UL),
            Assert.Single(sessionFactory.RestoredStates));
        Assert.Equal(
            checkpoint,
            Assert.Single(sink.ExpectedCheckpoints));
        Assert.Equal(
            "http://localhost:5000/sample?from=500",
            Assert.Single(handler.RequestUris).AbsoluteUri);
    }

    [Fact]
    public async Task CreateAsyncDetectsChangedInstanceAndRecovers()
    {
        var handler = new SequenceHandler(
            SampleResponse(instanceId: 43, nextSequence: 2),
            SampleResponse(instanceId: 43, nextSequence: 11));

        using var httpClient = new HttpClient(handler);
        var machineId = MachineId.New();
        var options = Options(machineId, fromSequence: 101);
        var checkpoint = new ObservationCheckpoint(
            MtConnectObservationStreamId.Create(
                machineId,
                options.DeviceKey),
            instanceId: 42,
            nextSequence: 500);
        var store = new InMemoryObservationIngestionStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                []));

        var sink = new RecordingSink();
        var reporter = new RecordingContinuityReporter();
        var sessionFactory = new RecordingSessionFactory(
            new MtConnectSampleClient(httpClient));
        var factory = CreateFactory(
            httpClient,
            store,
            sessionFactory,
            sink,
            options,
            reporter);

        var runtime = Assert.IsType<MtConnectAcquisitionRuntime>(
            await factory.CreateAsync());

        var result = await runtime.RunCycleAsync();

        Assert.Equal(43UL, result.InstanceId);
        Assert.Equal(
            (42UL, 500UL),
            Assert.Single(sessionFactory.RestoredStates));
        Assert.Equal(1UL, Assert.Single(sessionFactory.Sequences));

        var continuityLoss = Assert.Single(reporter.Losses);

        Assert.Equal(
            MtConnectContinuityLossReason.InstanceChanged,
            continuityLoss.Reason);
        Assert.Equal(42UL, continuityLoss.PreviousInstanceId);
        Assert.Equal(43UL, continuityLoss.CurrentInstanceId);
        Assert.Equal(500UL, continuityLoss.PreviousSequence);
        Assert.Equal(1UL, continuityLoss.RecoverySequence);
        Assert.Equal(
            checkpoint,
            Assert.Single(sink.ExpectedCheckpoints));
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(
            "http://localhost:5000/sample?from=500",
            handler.RequestUris[0].AbsoluteUri);
        Assert.Equal(
            "http://localhost:5000/sample?from=1",
            handler.RequestUris[1].AbsoluteUri);
    }

    private static MtConnectAcquisitionRuntimeFactory CreateFactory(
        HttpClient httpClient,
        IObservationIngestionStore store,
        IMtConnectAcquisitionSessionFactory sessionFactory,
        IMtConnectObservationSink sink,
        ulong fromSequence)
    {
        return CreateFactory(
            httpClient,
            store,
            sessionFactory,
            sink,
            Options(MachineId.New(), fromSequence));
    }

    private static MtConnectAcquisitionRuntimeFactory CreateFactory(
        HttpClient httpClient,
        IObservationIngestionStore store,
        IMtConnectAcquisitionSessionFactory sessionFactory,
        IMtConnectObservationSink sink,
        MtConnectAcquisitionOptions options,
        IMtConnectContinuityReporter? reporter = null)
    {
        var retryPolicy = new MtConnectTransientRetryPolicy(
            new MtConnectRetryOptions(
                1,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                0),
            new ImmediateRetryDelay(),
            new FixedJitterSource(),
            NullLogger<MtConnectTransientRetryPolicy>.Instance);

        return new MtConnectAcquisitionRuntimeFactory(
            options,
            new MtConnectStartupCheckpointResolver(store),
            sessionFactory,
            retryPolicy,
            new MtConnectContinuityRecoveryPolicy(
                sessionFactory,
                new MtConnectCurrentClient(httpClient),
                retryPolicy,
                reporter ?? new RecordingContinuityReporter()),
            sink);
    }

    private static MtConnectAcquisitionOptions Options(
        MachineId machineId,
        ulong fromSequence)
    {
        return new MtConnectAcquisitionOptions(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            machineId,
            "CNC-01",
            fromSequence,
            TimeSpan.FromSeconds(1));
    }

    private static HttpResponseMessage SampleResponse(
        ulong instanceId,
        ulong nextSequence)
    {
        var xml = $"""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="{instanceId}"
                      firstSequence="1"
                      lastSequence="{nextSequence - 1}"
                      nextSequence="{nextSequence}" />
              <Streams />
            </MTConnectStreams>
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        };
    }

    private sealed class RecordingSessionFactory(
        MtConnectSampleClient client)
        : IMtConnectAcquisitionSessionFactory
    {
        public List<ulong> Sequences { get; } = [];

        public List<(ulong InstanceId, ulong NextSequence)>
            RestoredStates { get; } = [];

        public MtConnectAcquisitionSession Create(ulong fromSequence)
        {
            Sequences.Add(fromSequence);

            return new MtConnectAcquisitionSession(
                client,
                fromSequence);
        }

        public MtConnectAcquisitionSession Restore(
            ulong instanceId,
            ulong nextSequence)
        {
            RestoredStates.Add((instanceId, nextSequence));

            return new MtConnectAcquisitionSession(
                client,
                instanceId,
                nextSequence);
        }
    }

    private sealed class RecordingSink : IMtConnectObservationSink
    {
        public List<ObservationCheckpoint?> ExpectedCheckpoints { get; } = [];

        public ValueTask WriteAsync(
            MtConnectSampleResult result,
            ObservationCheckpoint? expectedCheckpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpectedCheckpoints.Add(expectedCheckpoint);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingContinuityReporter :
        IMtConnectContinuityReporter
    {
        public List<MtConnectContinuityLoss> Losses { get; } = [];

        public ValueTask ReportAsync(
            MtConnectContinuityLoss continuityLoss,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Losses.Add(continuityLoss);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateRetryDelay : IMtConnectRetryDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    private sealed class FixedJitterSource : IMtConnectJitterSource
    {
        public double NextDouble() => 0.5;
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
            cancellationToken.ThrowIfCancellationRequested();

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
