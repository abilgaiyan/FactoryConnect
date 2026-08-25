using System.Net;
using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class MtConnectParserOutputContractTests
{
    [Fact]
    public async Task ManualXmlProducesSupportedPersistenceValueShapes()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Streams>
                <DeviceStream name="CNC-01" uuid="uuid-1">
                  <ComponentStream component="Controller" componentId="controller-1">
                    <Events>
                      <Execution dataItemId="exec" timestamp="2026-08-25T12:00:00+05:30">ACTIVE</Execution>
                      <Availability dataItemId="avail" timestamp="2026-08-25T12:00:01+05:30">UNAVAILABLE</Availability>
                    </Events>
                    <Condition>
                      <Fault dataItemId="fault" timestamp="2026-08-25T12:00:02+05:30">OVER_TEMPERATURE</Fault>
                    </Condition>
                  </ComponentStream>
                  <ComponentStream component="Rotary" componentId="rotary-1">
                    <Samples>
                      <Load dataItemId="load" timestamp="2026-08-25T12:00:03+05:30">1234.50</Load>
                    </Samples>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """;

        var observations = await CreateClient(xml).AcquireAsync(
            new MtConnectEndpoint(new Uri("http://localhost:5000")),
            MachineId.New(),
            "CNC-01");

        var execution = Assert.Single(
            observations,
            observation => observation.Address == "exec");
        Assert.Equal(SignalType.Enumeration, execution.Type);
        Assert.IsType<string>(execution.Value);
        Assert.Equal("ACTIVE", execution.Value);
        Assert.Equal(ObservationQuality.Good, execution.Quality);

        var availability = Assert.Single(
            observations,
            observation => observation.Address == "avail");
        Assert.Equal(SignalType.Enumeration, availability.Type);
        Assert.Null(availability.Value);
        Assert.Equal(ObservationQuality.Uncertain, availability.Quality);

        var fault = Assert.Single(
            observations,
            observation => observation.Address == "fault");
        Assert.Equal(SignalType.Text, fault.Type);
        Assert.IsType<string>(fault.Value);
        Assert.Equal("OVER_TEMPERATURE", fault.Value);
        Assert.Equal(ObservationQuality.Good, fault.Quality);

        var load = Assert.Single(
            observations,
            observation => observation.Address == "load");
        Assert.Equal(SignalType.Numeric, load.Type);
        Assert.IsType<decimal>(load.Value);
        Assert.Equal(1234.50m, load.Value);
        Assert.Equal(ObservationQuality.Good, load.Quality);
    }

    private static MtConnectCurrentClient CreateClient(string xml)
    {
        var httpClient = new HttpClient(
            new StubHandler(xml));

        return new MtConnectCurrentClient(httpClient);
    }

    private sealed class StubHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request,
            };

            return Task.FromResult(response);
        }
    }
}
