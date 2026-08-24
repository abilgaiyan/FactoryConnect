using System.Net;
using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectAcquisitionRuntimeContinuityTests
{
    [Fact]
    public async Task RunCycleAsyncRecoversOutOfRangeFromFirstSequence()
    {
        var handler = new SequenceHandler(
            ErrorResponse("OUT_OF_RANGE", 42),
            CurrentResponse(42, 500, 600, 601),
            SampleResponse(42, 500, 610, 611));

        using var httpClient = new HttpClient(handler);
        var reporter = new RecordingReporter();
        var sink = new RecordingSink();
        var runtime = CreateRuntime(httpClient, reporter, sink, 101);

        var result = await runtime.RunCycleAsync();

        Assert.Equal(611UL, result.NextSequence);
        Assert.Single(sink.Results);
        Assert.Single(reporter.Losses);
        Assert.Equal(
            [
                "http://localhost:5000/sample?from=101",
                "http://localhost:5000/current",
                "http://localhost:5000/sample?from=500",
            ],
            handler.RequestUris.Select(
                uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task RunCycleAsyncRecoversChangedAgentInstance()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 1, 110, 111),
            SampleResponse(43, 500, 510, 511),
            SampleResponse(43, 500, 520, 521));

        using var httpClient = new HttpClient(handler);
        var reporter = new RecordingReporter();
        var sink = new RecordingSink();
        var runtime = CreateRuntime(httpClient, reporter, sink, 101);

        await runtime.RunCycleAsync();
        var recovered = await runtime.RunCycleAsync();

        Assert.Equal(521UL, recovered.NextSequence);
        Assert.Equal(2, sink.Results.Count);
        Assert.Null(sink.ExpectedCheckpoints[0]);
        Assert.Equal(42UL, sink.ExpectedCheckpoints[1]?.InstanceId);
        Assert.Equal(111UL, sink.ExpectedCheckpoints[1]?.NextSequence);

        var loss = Assert.Single(reporter.Losses);
        Assert.Equal(
            MtConnectContinuityLossReason.InstanceChanged,
            loss.Reason);
        Assert.Equal(42UL, loss.PreviousInstanceId);
        Assert.Equal(43UL, loss.CurrentInstanceId);
        Assert.Equal(111UL, loss.PreviousSequence);
        Assert.Equal(500UL, loss.RecoverySequence);

        Assert.Equal(
            [
                "http://localhost:5000/sample?from=101",
                "http://localhost:5000/sample?from=111",
                "http://localhost:5000/sample?from=500",
            ],
            handler.RequestUris.Select(
                uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task RunCycleAsyncAllowsOnlyOneSessionReplacement()
    {
        var handler = new SequenceHandler(
            ErrorResponse("OUT_OF_RANGE", 42),
            CurrentResponse(42, 500, 600, 601),
            ErrorResponse("OUT_OF_RANGE", 42));

        using var httpClient = new HttpClient(handler);
        var reporter = new RecordingReporter();
        var runtime = CreateRuntime(
            httpClient,
            reporter,
            new RecordingSink(),
            101);

        await Assert.ThrowsAsync<MtConnectProtocolException>(
            () => runtime.RunCycleAsync());

        Assert.Single(reporter.Losses);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    private static MtConnectAcquisitionRuntime CreateRuntime(
        HttpClient httpClient,
        IMtConnectContinuityReporter reporter,
        IMtConnectObservationSink sink,
        ulong fromSequence)
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

        var sessionFactory =
            new MtConnectAcquisitionSessionFactory(
                new MtConnectSampleClient(httpClient));

        return new MtConnectAcquisitionRuntime(
            sessionFactory.Create(fromSequence),
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01",
            retryPolicy,
            new MtConnectContinuityRecoveryPolicy(
                sessionFactory,
                new MtConnectCurrentClient(httpClient),
                retryPolicy,
                reporter),
            sink,
            TimeSpan.FromSeconds(1));
    }

    private static HttpResponseMessage ErrorResponse(
        string errorCode,
        ulong instanceId)
    {
        var xml = $"""
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="{instanceId}" />
              <Errors>
                <Error errorCode="{errorCode}">
                  Continuity error.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(xml),
        };
    }

    private static HttpResponseMessage CurrentResponse(
        ulong instanceId,
        ulong firstSequence,
        ulong lastSequence,
        ulong nextSequence)
    {
        var xml = $"""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="{instanceId}"
                      firstSequence="{firstSequence}"
                      lastSequence="{lastSequence}"
                      nextSequence="{nextSequence}" />
              <Streams />
            </MTConnectStreams>
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        };
    }

    private static HttpResponseMessage SampleResponse(
        ulong instanceId,
        ulong firstSequence,
        ulong lastSequence,
        ulong nextSequence)
    {
        var xml = $"""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="{instanceId}"
                      firstSequence="{firstSequence}"
                      lastSequence="{lastSequence}"
                      nextSequence="{nextSequence}" />
              <Streams />
            </MTConnectStreams>
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        };
    }

    private sealed class RecordingReporter :
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

    private sealed class RecordingSink :
        IMtConnectObservationSink
    {
        public List<MtConnectSampleResult> Results { get; } = [];

        public List<ObservationCheckpoint?> ExpectedCheckpoints { get; } = [];

        public ValueTask WriteAsync(
            MtConnectSampleResult result,
            ObservationCheckpoint? expectedCheckpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Results.Add(result);
            ExpectedCheckpoints.Add(expectedCheckpoint);

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
