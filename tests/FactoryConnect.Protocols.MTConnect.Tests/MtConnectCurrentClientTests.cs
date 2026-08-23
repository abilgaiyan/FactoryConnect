using System.Net;
using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class MtConnectCurrentClientTests
{
    [Fact]
    public void EndpointBuildsCurrentUriFromConfiguredBaseAddress()
    {
        var endpoint = new MtConnectEndpoint(
            new Uri("http://192.168.100.50:5000/mtconnect"));

        Assert.Equal(
            "http://192.168.100.50:5000/mtconnect/current",
            endpoint.CurrentUri.AbsoluteUri);
    }

    [Fact]
    public async Task AcquireAsyncConvertsEventsAndSamplesToMachineObservations()
    {
        var machineId = MachineId.New();
        var result = await CreateClient(CurrentXml()).AcquireAsync(
            new MtConnectEndpoint(new Uri("http://localhost:5000")),
            machineId,
            "CNC-01");

        Assert.Equal(2, result.Count);

        var execution = Assert.Single(
            result,
            observation => observation.Address == "exec");
        Assert.Equal(machineId, execution.MachineId);
        Assert.Equal("mtconnect", execution.Source);
        Assert.Equal(SignalType.Enumeration, execution.Type);
        Assert.Equal("ACTIVE", execution.Value);
        Assert.Equal(ObservationQuality.Good, execution.Quality);

        var load = Assert.Single(
            result,
            observation => observation.Address == "load");
        Assert.Equal(SignalType.Numeric, load.Type);
        Assert.Equal(42.5m, load.Value);
    }


    [Fact]
    public async Task AcquireResultAsyncPreservesHeaderMetadata()
    {
        var result = await CreateClient(CurrentXml())
            .AcquireResultAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01");

        Assert.Equal(42UL, result.InstanceId);
        Assert.Equal(1UL, result.FirstSequence);
        Assert.Equal(100UL, result.LastSequence);
        Assert.Equal(101UL, result.NextSequence);
        Assert.Equal(2, result.Observations.Count);
    }

    [Fact]
    public async Task AcquireResultAsyncRequiresHeader()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Streams />
            </MTConnectStreams>
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateClient(xml).AcquireResultAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01"));
    }

    [Fact]
    public async Task AcquireResultAsyncRequiresSequenceMetadata()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="42"
                      firstSequence="1"
                      lastSequence="100" />
              <Streams />
            </MTConnectStreams>
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateClient(xml).AcquireResultAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01"));
    }


    [Fact]
    public async Task AcquireResultAsyncPreservesMtConnectProtocolError()
    {
        var client = CreateClient(
            MtConnectErrorXml(42, "OUT_OF_RANGE", "Sequence is outside the buffer."),
            HttpStatusCode.NotFound);

        var exception =
            await Assert.ThrowsAsync<MtConnectProtocolException>(
                () => client.AcquireResultAsync(
                    new MtConnectEndpoint(new Uri("http://localhost:5000")),
                    MachineId.New(),
                    "CNC-01"));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(42UL, exception.ErrorResult.InstanceId);
        var error = Assert.Single(exception.ErrorResult.Errors);
        Assert.Equal("OUT_OF_RANGE", error.Code);
        Assert.Equal("Sequence is outside the buffer.", error.Message);
    }

    [Fact]
    public async Task AcquireAsyncPreservesMtConnectProtocolError()
    {
        var client = CreateClient(
            MtConnectErrorXml(42, "NO_DEVICE", "Device was not found."),
            HttpStatusCode.NotFound);

        var exception =
            await Assert.ThrowsAsync<MtConnectProtocolException>(
                () => client.AcquireAsync(
                    new MtConnectEndpoint(new Uri("http://localhost:5000")),
                    MachineId.New(),
                    "CNC-01"));

        Assert.Equal(
            "NO_DEVICE",
            Assert.Single(exception.ErrorResult.Errors).Code);
    }


    [Fact]
    public async Task AcquireResultAsyncRejectsMalformedMtConnectProtocolError()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="42" />
              <Errors>
                <Error>
                  Missing error code.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        var client = CreateClient(
            xml,
            HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.AcquireResultAsync(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01"));
    }

    [Fact]
    public async Task AcquireResultAsyncPreservesOrdinaryHttpFailure()
    {
        var client = CreateClient(
            "Service unavailable.",
            HttpStatusCode.ServiceUnavailable);

        var exception =
            await Assert.ThrowsAsync<HttpRequestException>(
                () => client.AcquireResultAsync(
                    new MtConnectEndpoint(new Uri("http://localhost:5000")),
                    MachineId.New(),
                    "CNC-01"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task AcquireAsyncMarksUnavailableValuesAsUncertain()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Streams>
                <DeviceStream name="CNC-01" uuid="uuid-1">
                  <ComponentStream component="Controller" componentId="c1">
                    <Events>
                      <Availability dataItemId="avail" timestamp="2026-08-21T10:00:00Z">UNAVAILABLE</Availability>
                    </Events>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;

        var result = await CreateClient(xml).AcquireAsync(
            new MtConnectEndpoint(new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01");

        var observation = Assert.Single(result);
        Assert.Null(observation.Value);
        Assert.Equal(ObservationQuality.Uncertain, observation.Quality);
    }

    [Fact]
    public async Task AcquireAsyncCanSelectDeviceStreamByUuid()
    {
        var result = await CreateClient(CurrentXml()).AcquireAsync(
            new MtConnectEndpoint(new Uri("http://localhost:5000")),
            MachineId.New(),
            "uuid-1");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task AcquireAsyncRejectsNonNumericSampleValue()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Streams>
                <DeviceStream name="CNC-01">
                  <ComponentStream component="Rotary" componentId="r1">
                    <Samples>
                      <Load dataItemId="load" timestamp="2026-08-21T10:00:00Z">invalid</Load>
                    </Samples>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateClient(xml).AcquireAsync(
                new MtConnectEndpoint(new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01"));
    }

    private static MtConnectCurrentClient CreateClient(
        string xml,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var httpClient = new HttpClient(
            new StubHandler(statusCode, xml));

        return new MtConnectCurrentClient(httpClient);
    }

    private static string MtConnectErrorXml(
        ulong instanceId,
        string errorCode,
        string message)
    {
        return $"""
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="{instanceId}" />
              <Errors>
                <Error errorCode="{errorCode}">
                  {message}
                </Error>
              </Errors>
            </MTConnectError>
            """;
    }

    private static string CurrentXml() => """
        <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
          <Header instanceId="42" nextSequence="101" firstSequence="1" lastSequence="100" />
          <Streams>
            <DeviceStream name="CNC-01" uuid="uuid-1">
              <ComponentStream component="Controller" componentId="controller-1">
                <Events>
                  <Execution dataItemId="exec" timestamp="2026-08-21T10:00:00Z" sequence="99">ACTIVE</Execution>
                </Events>
              </ComponentStream>
              <ComponentStream component="Rotary" componentId="rotary-1">
                <Samples>
                  <Load dataItemId="load" timestamp="2026-08-21T10:00:01Z" sequence="100">42.5</Load>
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
                RequestMessage = request,
            };

            return Task.FromResult(response);
        }
    }
}
