using System.Net;
using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class MtConnectSampleClientTests
{
    [Fact]
    public void EndpointBuildsSampleUriFromConfiguredBaseAddress()
    {
        var endpoint = new MtConnectEndpoint(
            new Uri("http://192.168.100.50:5000/mtconnect"));

        Assert.Equal(
            "http://192.168.100.50:5000/mtconnect/sample?from=101",
            endpoint.SampleUri(101).AbsoluteUri);
    }

    [Fact]
    public async Task AcquireAsyncReturnsSequenceMetadata()
    {
        var result = await CreateClient(SampleXml()).AcquireAsync(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01",
            99);

        Assert.Equal(42UL, result.InstanceId);
        Assert.Equal(1UL, result.FirstSequence);
        Assert.Equal(104UL, result.LastSequence);
        Assert.Equal(105UL, result.NextSequence);
    }

    [Fact]
    public async Task AcquireAsyncReturnsObservationSequences()
    {
        var result = await CreateClient(SampleXml()).AcquireAsync(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01",
            99);

        Assert.Equal(4, result.Observations.Count);

        Assert.Equal(
            [101UL, 102UL, 103UL, 104UL],
            result.Observations
                .Select(observation => observation.Sequence)
                .ToArray());
    }

    [Fact]
    public async Task AcquireAsyncOrdersObservationsBySequence()
    {
        var result = await CreateClient(
                OutOfDocumentOrderSampleXml())
            .AcquireAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01",
                100);

        Assert.Equal(
            [101UL, 102UL, 103UL, 104UL],
            result.Observations
                .Select(observation => observation.Sequence)
                .ToArray());
    }

    [Fact]
    public async Task AcquireAsyncConvertsEventsAndSamplesToMachineObservations()
    {
        var machineId = MachineId.New();

        var result = await CreateClient(SampleXml()).AcquireAsync(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            machineId,
            "CNC-01",
            99);

        var execution = Assert.Single(
            result.Observations,
            item => item.Observation.Address == "exec");

        Assert.Equal(101UL, execution.Sequence);
        Assert.Equal(machineId, execution.Observation.MachineId);
        Assert.Equal("mtconnect", execution.Observation.Source);
        Assert.Equal(
            SignalType.Enumeration,
            execution.Observation.Type);
        Assert.Equal(
            "ACTIVE",
            execution.Observation.Value);

        var load = Assert.Single(
            result.Observations,
            item => item.Observation.Address == "load");

        Assert.Equal(SignalType.Numeric, load.Observation.Type);
        Assert.Equal(42.5m, load.Observation.Value);
    }

    [Fact]
    public async Task AcquireAsyncMarksUnavailableValuesAsUncertain()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="42"
                      firstSequence="1"
                      lastSequence="101"
                      nextSequence="102" />
              <Streams>
                <DeviceStream name="CNC-01" uuid="uuid-1">
                  <ComponentStream component="Controller" componentId="c1">
                    <Events>
                      <Availability dataItemId="avail"
                                    timestamp="2026-08-22T10:00:00Z"
                                    sequence="101">UNAVAILABLE</Availability>
                    </Events>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;

        var result = await CreateClient(xml).AcquireAsync(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01",
            101);

        var item = Assert.Single(result.Observations);

        Assert.Null(item.Observation.Value);
        Assert.Equal(
            ObservationQuality.Uncertain,
            item.Observation.Quality);
    }

    [Fact]
    public async Task AcquireAsyncCanSelectDeviceStreamByUuid()
    {
        var result = await CreateClient(SampleXml()).AcquireAsync(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "uuid-1",
            99);

        Assert.Equal(4, result.Observations.Count);
    }

    [Fact]
    public async Task AcquireAsyncReturnsEmptyObservationsWhenDeviceIsNotFound()
    {
        var result = await CreateClient(SampleXml()).AcquireAsync(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "missing-device",
            99);

        Assert.Empty(result.Observations);

        Assert.Equal(42UL, result.InstanceId);
        Assert.Equal(105UL, result.NextSequence);
    }

    [Fact]
    public async Task AcquireAsyncRejectsObservationWithoutSequence()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="42"
                      firstSequence="1"
                      lastSequence="100"
                      nextSequence="101" />
              <Streams>
                <DeviceStream name="CNC-01">
                  <ComponentStream component="Controller" componentId="c1">
                    <Events>
                      <Execution dataItemId="exec"
                                 timestamp="2026-08-22T10:00:00Z">ACTIVE</Execution>
                    </Events>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateClient(xml).AcquireAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01",
                100));
    }

    [Fact]
    public async Task AcquireAsyncRejectsInvalidHeaderSequenceMetadata()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="42"
                      firstSequence="invalid"
                      lastSequence="100"
                      nextSequence="101" />
              <Streams />
            </MTConnectStreams>
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateClient(xml).AcquireAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01",
                100));
    }

    [Fact]
    public async Task AcquireAsyncPropagatesHttpFailure()
    {
        using var httpClient = new HttpClient(
            new StubHandler(
                HttpStatusCode.ServiceUnavailable,
                string.Empty));

        var client = new MtConnectSampleClient(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.AcquireAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01",
                100));
    }

    [Fact]
    public async Task AcquireAsyncRequestsSampleFromSuppliedSequence()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            SampleXml());

        using var httpClient = new HttpClient(handler);
        var client = new MtConnectSampleClient(httpClient);

        await client.AcquireAsync(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01",
            101);

        Assert.Equal(
            "http://localhost:5000/sample?from=101",
            handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task AcquireAsyncRejectsInvalidObservationSequence()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
            <Header instanceId="42"
                    firstSequence="1"
                    lastSequence="100"
                    nextSequence="101" />
            <Streams>
                <DeviceStream name="CNC-01">
                <ComponentStream component="Controller" componentId="c1">
                    <Events>
                    <Execution dataItemId="exec"
                                timestamp="2026-08-22T10:00:00Z"
                                sequence="invalid">ACTIVE</Execution>
                    </Events>
                </ComponentStream>
                </DeviceStream>
            </Streams>
            </MTConnectStreams>
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateClient(xml).AcquireAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01",
                100UL));
    }

    private static MtConnectSampleClient CreateClient(
        string xml)
    {
        var httpClient = new HttpClient(
            new StubHandler(
                HttpStatusCode.OK,
                xml));

        return new MtConnectSampleClient(httpClient);
    }

    private static string SampleXml() => """
        <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
          <Header instanceId="42"
                  nextSequence="105"
                  firstSequence="1"
                  lastSequence="104" />
          <Streams>
            <DeviceStream name="CNC-01" uuid="uuid-1">
              <ComponentStream component="Controller" componentId="controller-1">
                <Events>
                  <Execution dataItemId="exec"
                             timestamp="2026-08-22T10:00:00Z"
                             sequence="101">ACTIVE</Execution>
                  <ControllerMode dataItemId="mode"
                                  timestamp="2026-08-22T10:00:01Z"
                                  sequence="102">AUTOMATIC</ControllerMode>
                </Events>
              </ComponentStream>
              <ComponentStream component="Rotary" componentId="rotary-1">
                <Samples>
                  <Load dataItemId="load"
                        timestamp="2026-08-22T10:00:02Z"
                        sequence="103">42.5</Load>
                  <RotaryVelocity dataItemId="speed"
                                  timestamp="2026-08-22T10:00:03Z"
                                  sequence="104">1250</RotaryVelocity>
                </Samples>
              </ComponentStream>
            </DeviceStream>
          </Streams>
        </MTConnectStreams>
        """;

    private static string OutOfDocumentOrderSampleXml() => """
        <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
          <Header instanceId="42"
                  nextSequence="105"
                  firstSequence="1"
                  lastSequence="104" />
          <Streams>
            <DeviceStream name="CNC-01" uuid="uuid-1">
              <ComponentStream component="Controller" componentId="controller-1">
                <Events>
                  <Execution dataItemId="exec"
                             timestamp="2026-08-22T10:00:00Z"
                             sequence="101">ACTIVE</Execution>
                  <ControllerMode dataItemId="mode"
                                  timestamp="2026-08-22T10:00:03Z"
                                  sequence="104">AUTOMATIC</ControllerMode>
                </Events>
              </ComponentStream>
              <ComponentStream component="Rotary" componentId="rotary-1">
                <Samples>
                  <Load dataItemId="load"
                        timestamp="2026-08-22T10:00:01Z"
                        sequence="102">42.5</Load>
                  <RotaryVelocity dataItemId="speed"
                                  timestamp="2026-08-22T10:00:02Z"
                                  sequence="103">1250</RotaryVelocity>
                </Samples>
              </ComponentStream>
            </DeviceStream>
          </Streams>
        </MTConnectStreams>
        """;

    private sealed class StubHandler(
        HttpStatusCode statusCode,
        string content) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
                RequestMessage = request,
            };

            return Task.FromResult(response);
        }
    }
}
