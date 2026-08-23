using System.Net;
using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class MtConnectAcquisitionSessionTests
{
    [Fact]
    public void ConstructorUsesInitialSequence()
    {
        using var httpClient = new HttpClient(
            new SequenceHandler());

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        Assert.Null(session.InstanceId);
        Assert.Equal(101UL, session.NextSequence);
    }

    [Fact]
    public async Task AcquireNextAsyncUsesInitialSequence()
    {
        var handler = new SequenceHandler(
            SampleResponse(
                instanceId: 42,
                firstSequence: 1,
                lastSequence: 110,
                nextSequence: 111));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            Assert.Single(handler.RequestUris).AbsoluteUri);
    }

    [Fact]
    public async Task AcquireNextAsyncAdvancesToReturnedNextSequence()
    {
        var handler = new SequenceHandler(
            SampleResponse(
                instanceId: 42,
                firstSequence: 1,
                lastSequence: 110,
                nextSequence: 111));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        Assert.Equal(42UL, session.InstanceId);
        Assert.Equal(111UL, session.NextSequence);
    }

    [Fact]
    public async Task SecondAcquisitionUsesPreviousNextSequence()
    {
        var handler = new SequenceHandler(
            SampleResponse(
                instanceId: 42,
                firstSequence: 1,
                lastSequence: 110,
                nextSequence: 111),
            SampleResponse(
                instanceId: 42,
                firstSequence: 1,
                lastSequence: 120,
                nextSequence: 121));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        Assert.Equal(2, handler.RequestUris.Count);

        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            handler.RequestUris[0].AbsoluteUri);

        Assert.Equal(
            "http://localhost:5000/sample?from=111",
            handler.RequestUris[1].AbsoluteUri);

        Assert.Equal(121UL, session.NextSequence);
    }

    [Fact]
    public async Task AcquireNextAsyncAllowsSameInstanceId()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 1, 110, 111),
            SampleResponse(42, 1, 120, 121));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        Assert.Equal(42UL, session.InstanceId);
        Assert.Equal(121UL, session.NextSequence);
    }

    [Fact]
    public async Task AcquireNextAsyncRejectsChangedInstanceId()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 1, 110, 111),
            SampleResponse(43, 1, 120, 121));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.AcquireNextAsync(
                Endpoint(),
                MachineId.New(),
                "CNC-01"));

        Assert.Equal(42UL, session.InstanceId);
        Assert.Equal(111UL, session.NextSequence);
    }

    [Fact]
    public async Task FailedAcquisitionDoesNotAdvanceSession()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => session.AcquireNextAsync(
                Endpoint(),
                MachineId.New(),
                "CNC-01"));

        Assert.Null(session.InstanceId);
        Assert.Equal(101UL, session.NextSequence);
    }

    [Fact]
    public async Task CancellationDoesNotAdvanceSession()
    {
        using var httpClient = new HttpClient(
            new CancellingHandler());

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.AcquireNextAsync(
                Endpoint(),
                MachineId.New(),
                "CNC-01",
                cancellation.Token));

        Assert.Null(session.InstanceId);
        Assert.Equal(101UL, session.NextSequence);
    }

    [Fact]
    public async Task FailedSubsequentAcquisitionPreservesEstablishedSession()
    {
        var handler = new SequenceHandler(
            SampleResponse(42, 1, 110, 111),
            new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        Assert.Equal(42UL, session.InstanceId);
        Assert.Equal(111UL, session.NextSequence);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => session.AcquireNextAsync(
                Endpoint(),
                MachineId.New(),
                "CNC-01"));

        Assert.Equal(42UL, session.InstanceId);
        Assert.Equal(111UL, session.NextSequence);
    }

    [Fact]
    public async Task ProtocolErrorDoesNotAdvanceInitialSession()
    {
        var handler = new SequenceHandler(
            MtConnectErrorResponse(
                HttpStatusCode.NotFound,
                instanceId: 42,
                errorCode: "OUT_OF_RANGE",
                message: "Requested sequence is outside the available range."));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await Assert.ThrowsAsync<MtConnectProtocolException>(
            () => session.AcquireNextAsync(
                Endpoint(),
                MachineId.New(),
                "CNC-01"));

        Assert.Null(session.InstanceId);
        Assert.Equal(101UL, session.NextSequence);
    }

    [Fact]
    public async Task ProtocolErrorDoesNotAdvanceEstablishedSession()
    {
        var handler = new SequenceHandler(
            SampleResponse(
                instanceId: 42,
                firstSequence: 1,
                lastSequence: 110,
                nextSequence: 111),
            MtConnectErrorResponse(
                HttpStatusCode.NotFound,
                instanceId: 42,
                errorCode: "OUT_OF_RANGE",
                message: "Requested sequence is outside the available range."));

        using var httpClient = new HttpClient(handler);

        var session = new MtConnectAcquisitionSession(
            new MtConnectSampleClient(httpClient),
            101);

        await session.AcquireNextAsync(
            Endpoint(),
            MachineId.New(),
            "CNC-01");

        Assert.Equal(42UL, session.InstanceId);
        Assert.Equal(111UL, session.NextSequence);

        await Assert.ThrowsAsync<MtConnectProtocolException>(
            () => session.AcquireNextAsync(
                Endpoint(),
                MachineId.New(),
                "CNC-01"));

        Assert.Equal(42UL, session.InstanceId);
        Assert.Equal(111UL, session.NextSequence);
    }

    private static HttpResponseMessage MtConnectErrorResponse(
        HttpStatusCode statusCode,
        ulong? instanceId,
        string errorCode,
        string message)
    {
        var instanceIdAttribute = instanceId is null
            ? string.Empty
            : $" instanceId=\"{instanceId.Value}\"";

        var xml = $"""
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
            <Header{instanceIdAttribute} />
            <Errors>
                <Error errorCode="{errorCode}">
                {message}
                </Error>
            </Errors>
            </MTConnectError>
            """;

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(xml),
        };
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

    private static MtConnectEndpoint Endpoint() =>
        new(new Uri("http://localhost:5000"));

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
              <Streams>
                <DeviceStream name="CNC-01" uuid="uuid-1">
                  <ComponentStream component="Controller" componentId="c1">
                    <Events>
                      <Execution dataItemId="exec"
                                 timestamp="2026-08-22T10:00:00Z"
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

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public List<Uri> RequestUris { get; } = [];

        public SequenceHandler(
            params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(
                responses);
        }

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
