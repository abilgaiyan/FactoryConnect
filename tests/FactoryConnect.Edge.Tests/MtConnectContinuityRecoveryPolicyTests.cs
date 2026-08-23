using System.Net;
using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectContinuityRecoveryPolicyTests
{
    [Fact]
    public async Task RecoverOutOfRangeAsyncUsesCurrentFirstSequence()
    {
        var handler = new SequenceHandler(
            CurrentResponse(
                instanceId: 43,
                firstSequence: 500,
                lastSequence: 600,
                nextSequence: 601));

        using var httpClient = new HttpClient(handler);
        var reporter = new RecordingReporter();
        var policy = CreatePolicy(httpClient, reporter);
        var previousSession = CreateSession(
            httpClient,
            fromSequence: 101);

        var replacement =
            await policy.RecoverOutOfRangeAsync(
                OutOfRangeException(instanceId: 43),
                previousSession,
                Endpoint(),
                MachineId.New(),
                "CNC-01");

        Assert.Equal(500UL, replacement.NextSequence);

        var continuityLoss =
            Assert.Single(reporter.Losses);

        Assert.Equal(
            MtConnectContinuityLossReason.OutOfRange,
            continuityLoss.Reason);
        Assert.Null(continuityLoss.PreviousInstanceId);
        Assert.Equal(43UL, continuityLoss.CurrentInstanceId);
        Assert.Equal(101UL, continuityLoss.PreviousSequence);
        Assert.Equal(500UL, continuityLoss.RecoverySequence);
    }

    [Fact]
    public async Task RecoverOutOfRangeAsyncRetriesCurrentAcquisition()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable),
            CurrentResponse(43, 500, 600, 601));

        using var httpClient = new HttpClient(handler);
        var reporter = new RecordingReporter();
        var policy = CreatePolicy(
            httpClient,
            reporter,
            maxAttempts: 2);

        var replacement =
            await policy.RecoverOutOfRangeAsync(
                OutOfRangeException(instanceId: 43),
                CreateSession(httpClient, 101),
                Endpoint(),
                MachineId.New(),
                "CNC-01");

        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(500UL, replacement.NextSequence);
        Assert.Single(reporter.Losses);
    }

    [Fact]
    public async Task RecoverOutOfRangeAsyncRejectsOtherProtocolErrors()
    {
        using var httpClient = new HttpClient(
            new SequenceHandler());
        var reporter = new RecordingReporter();
        var policy = CreatePolicy(httpClient, reporter);

        var exception = new MtConnectProtocolException(
            HttpStatusCode.BadRequest,
            new MtConnectErrorResult
            {
                InstanceId = 42,
                Errors =
                [
                    new MtConnectError
                    {
                        Code = "INVALID_REQUEST",
                        Message = "Invalid request.",
                    },
                ],
            });

        await Assert.ThrowsAsync<ArgumentException>(
            () => policy.RecoverOutOfRangeAsync(
                exception,
                CreateSession(httpClient, 101),
                Endpoint(),
                MachineId.New(),
                "CNC-01"));

        Assert.Empty(reporter.Losses);
    }

    [Fact]
    public async Task RecoverInstanceChangeAsyncUsesExceptionMetadata()
    {
        using var httpClient = new HttpClient(
            new SequenceHandler());
        var reporter = new RecordingReporter();
        var policy = CreatePolicy(httpClient, reporter);
        var machineId = MachineId.New();

        var replacement =
            await policy.RecoverInstanceChangeAsync(
                new MtConnectInstanceChangedException(
                    previousInstanceId: 42,
                    currentInstanceId: 43,
                    firstSequence: 500),
                CreateSession(httpClient, 111),
                machineId);

        Assert.Equal(500UL, replacement.NextSequence);

        var continuityLoss =
            Assert.Single(reporter.Losses);

        Assert.Equal(machineId, continuityLoss.MachineId);
        Assert.Equal(
            MtConnectContinuityLossReason.InstanceChanged,
            continuityLoss.Reason);
        Assert.Equal(42UL, continuityLoss.PreviousInstanceId);
        Assert.Equal(43UL, continuityLoss.CurrentInstanceId);
        Assert.Equal(111UL, continuityLoss.PreviousSequence);
        Assert.Equal(500UL, continuityLoss.RecoverySequence);
    }

    private static MtConnectContinuityRecoveryPolicy CreatePolicy(
        HttpClient httpClient,
        IMtConnectContinuityReporter reporter,
        int maxAttempts = 1)
    {
        return new MtConnectContinuityRecoveryPolicy(
            new MtConnectAcquisitionSessionFactory(
                new MtConnectSampleClient(httpClient)),
            new MtConnectCurrentClient(httpClient),
            new MtConnectTransientRetryPolicy(
                new MtConnectRetryOptions(
                    maxAttempts,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1),
                    0),
                new ImmediateRetryDelay(),
                new FixedJitterSource(),
                NullLogger<MtConnectTransientRetryPolicy>.Instance),
            reporter);
    }

    private static MtConnectAcquisitionSession CreateSession(
        HttpClient httpClient,
        ulong fromSequence)
    {
        return new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            fromSequence);
    }

    private static MtConnectProtocolException OutOfRangeException(
        ulong instanceId)
    {
        return new MtConnectProtocolException(
            HttpStatusCode.NotFound,
            new MtConnectErrorResult
            {
                InstanceId = instanceId,
                Errors =
                [
                    new MtConnectError
                    {
                        Code = "OUT_OF_RANGE",
                        Message = "Sequence is outside the buffer.",
                    },
                ],
            });
    }

    private static MtConnectEndpoint Endpoint() =>
        new(new Uri("http://localhost:5000"));

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
