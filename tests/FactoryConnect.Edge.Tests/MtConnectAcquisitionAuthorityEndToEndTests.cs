using System.Net;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectAcquisitionAuthorityEndToEndTests
{
    [Fact]
    public async Task DurableRetryCommitsSingleCapturedContactAuthorityWithoutReacquisition()
    {
        var handler = new SequenceHandler(SampleResponse(42, 110, 111));
        using var httpClient = new HttpClient(handler);
        var machineId = MachineId.New();
        var streamId = MtConnectObservationStreamId.Create(machineId, "CNC-01");
        var store = new FailOnceObservationIngestionStore();
        var contactTime = new DateTimeOffset(2026, 9, 12, 4, 30, 0, TimeSpan.Zero);
        var timeProvider = new CountingTimeProvider(contactTime);
        var runtime = CreateRuntime(
            httpClient,
            new MtConnectDurableObservationSink(store, streamId),
            machineId,
            timeProvider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        Assert.Single(handler.RequestUris);
        Assert.Equal(1, timeProvider.ReadCount);
        Assert.Null(await store.Inner.ReadCheckpointAsync(streamId));
        Assert.Null(await store.Inner.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Empty(store.Inner.ReadObservations(streamId));

        await runtime.RunCycleAsync();

        Assert.Single(handler.RequestUris);
        Assert.Equal(1, timeProvider.ReadCount);

        var checkpoint = await store.Inner.ReadCheckpointAsync(streamId);
        Assert.NotNull(checkpoint);
        Assert.Equal(42UL, checkpoint.InstanceId);
        Assert.Equal(111UL, checkpoint.NextSequence);

        var authority = await store.Inner.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);
        Assert.Equal(streamId, authority.ObservationStreamId);
        Assert.Equal(contactTime.UtcDateTime.Ticks, authority.SuccessfulContactTime.UtcDateTime.Ticks);
        Assert.NotNull(authority.RawAcceptedThrough);
        Assert.Single(store.Inner.ReadObservations(streamId));
    }

    private static MtConnectAcquisitionRuntime CreateRuntime(
        HttpClient httpClient,
        IMtConnectObservationSink sink,
        MachineId machineId,
        TimeProvider timeProvider) =>
        new(
            new MtConnectAcquisitionSession(
                new MtConnectSampleClient(httpClient),
                101),
            new MtConnectEndpoint(new Uri("http://localhost:5000")),
            machineId,
            "CNC-01",
            new MtConnectTransientRetryPolicy(
                new MtConnectRetryOptions(
                    1,
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
            timeProvider,
            TimeSpan.FromMilliseconds(1));

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
                                 timestamp="2026-09-12T04:29:00Z"
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

    private sealed class FailOnceObservationIngestionStore : IObservationIngestionStore
    {
        private bool _failNextCommit = true;

        public InMemoryObservationIngestionStore Inner { get; } = new();

        public ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default) =>
            Inner.ReadCheckpointAsync(streamId, cancellationToken);

        public ValueTask<AcquisitionContactAuthority?> ReadAcquisitionContactAuthorityAsync(
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAcquisitionContactAuthorityAsync(streamId, cancellationToken);

        public ValueTask CommitAsync(
            ObservationIngestionBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_failNextCommit)
            {
                _failNextCommit = false;
                throw new InvalidOperationException("Store commit failed.");
            }

            return Inner.CommitAsync(batch, cancellationToken);
        }
    }

    private sealed class CountingTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return utcNow;
        }
    }

    private sealed class IgnoringContinuityReporter : IMtConnectContinuityReporter
    {
        public ValueTask ReportAsync(
            MtConnectContinuityLoss continuityLoss,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

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
                throw new InvalidOperationException("No HTTP response configured for request.");
            }

            var response = _responses.Dequeue();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
