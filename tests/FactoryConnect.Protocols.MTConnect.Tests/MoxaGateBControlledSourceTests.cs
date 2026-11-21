using System.Net;
using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

/// <summary>
/// Protocol-level Gate B fixture only. Does not write SQL or prove activity publication.
/// </summary>
public sealed class MoxaGateBControlledSourceTests
{
    private const ulong InstanceId = 1793867780;
    private const ulong NextSequence = 27;
    private static readonly Uri EndpointUri = new("http://localhost:51027/");

    [Theory]
    [InlineData("RUNNING")]
    [InlineData("IDLE")]
    [InlineData("STOPPED")]
    [InlineData("FAULT")]
    public async Task RestoredMoxaSessionAcceptsSameInstanceStatus(string status)
    {
        var handler = new ControlledSourceHandler(InstanceId, status);
        using var http = new HttpClient(handler);
        var session = new MtConnectAcquisitionSession(new MtConnectSampleClient(http), InstanceId, NextSequence);

        var result = await session.PrepareNextAsync(
            new MtConnectEndpoint(EndpointUri),
            MachineId.New(),
            "DEWAS-SR-MOXA-001");

        Assert.Equal(NextSequence, handler.RequestedFrom);
        Assert.Equal(InstanceId, result.InstanceId);
        Assert.Equal(28UL, result.NextSequence);
        var observation = Assert.Single(result.Observations);
        Assert.Equal(27UL, observation.Sequence);
        Assert.Equal("mtconnect", observation.Observation.Source);
        Assert.Equal("moxa001_status", observation.Observation.Address);
        Assert.Equal(SignalType.Enumeration, observation.Observation.Type);
        Assert.Equal(ObservationQuality.Good, observation.Observation.Quality);
        Assert.Equal(status, observation.Observation.Value);

        // Prepare must not mutate the cursor before durable publication.
        Assert.Equal(NextSequence, session.NextSequence);
        session.Advance(result);
        Assert.Equal(28UL, session.NextSequence);
    }

    [Fact]
    public async Task RestoredMoxaSessionRejectsDifferentInstanceBeforeAdvance()
    {
        var handler = new ControlledSourceHandler(InstanceId + 1, "RUNNING");
        using var http = new HttpClient(handler);
        var session = new MtConnectAcquisitionSession(new MtConnectSampleClient(http), InstanceId, NextSequence);

        await Assert.ThrowsAsync<MtConnectInstanceChangedException>(
            () => session.PrepareNextAsync(
                new MtConnectEndpoint(EndpointUri),
                MachineId.New(),
                "DEWAS-SR-MOXA-001"));

        Assert.Equal(NextSequence, handler.RequestedFrom);
        Assert.Equal(NextSequence, session.NextSequence);
        Assert.Equal(InstanceId, session.InstanceId);
    }

    [Fact]
    public async Task UnavailableStatusRemainsUncertainAndNull()
    {
        var handler = new ControlledSourceHandler(InstanceId, "UNAVAILABLE");
        using var http = new HttpClient(handler);
        var session = new MtConnectAcquisitionSession(new MtConnectSampleClient(http), InstanceId, NextSequence);
        var result = await session.PrepareNextAsync(
            new MtConnectEndpoint(EndpointUri), MachineId.New(), "DEWAS-SR-MOXA-001");
        var item = Assert.Single(result.Observations);
        Assert.Null(item.Observation.Value);
        Assert.Equal(ObservationQuality.Uncertain, item.Observation.Quality);
    }

    private sealed class ControlledSourceHandler(ulong instanceId, string value) : HttpMessageHandler
    {
        public ulong? RequestedFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("localhost", request.RequestUri?.Host);
            Assert.Equal(51027, request.RequestUri?.Port);
            Assert.Equal("/sample", request.RequestUri?.AbsolutePath);
            Assert.Equal("?from=27", request.RequestUri?.Query);
            RequestedFrom = 27;
            var xml = $"""
                <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
                  <Header instanceId="{instanceId}" firstSequence="27" lastSequence="27" nextSequence="28" />
                  <Streams>
                    <DeviceStream name="DEWAS-SR-MOXA-001">
                      <ComponentStream component="Controller" componentId="moxa001">
                        <Events>
                          <Execution dataItemId="moxa001_status"
                            timestamp="2026-11-05T08:36:21Z" sequence="27">{value}</Execution>
                        </Events>
                      </ComponentStream>
                    </DeviceStream>
                  </Streams>
                </MTConnectStreams>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml)
            });
        }
    }
}
