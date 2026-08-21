using System.Net;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class MtConnectDiscoveryClientTests
{
    [Fact]
    public void EndpointBuildsProbeUriFromConfiguredBaseAddress()
    {
        var endpoint = new MtConnectEndpoint(new Uri("http://192.168.100.50:5000/mtconnect"));

        Assert.Equal("http://192.168.100.50:5000/mtconnect/probe", endpoint.ProbeUri.AbsoluteUri);
    }

    [Fact]
    public async Task DiscoverAsyncParsesNamespacedProbeDocument()
    {
        var client = CreateClient(ProbeXml());

        var result = await client.DiscoverAsync(
            new MtConnectEndpoint(new Uri("http://localhost:5000")));

        Assert.Equal("42", result.AgentInstanceId);
        Assert.Equal("2.5.0", result.AgentVersion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("dev-1", device.Id);
        Assert.Equal("CNC-01", device.Name);
        Assert.Equal(2, device.DataItems.Count);
        Assert.Contains(device.DataItems, item => item.Type == "EXECUTION");
        Assert.Contains(device.DataItems, item => item.Type == "AVAILABILITY");
    }

    [Fact]
    public async Task DiscoverAsyncPreservesComponentMetadata()
    {
        var result = await CreateClient(ProbeXml()).DiscoverAsync(
            new MtConnectEndpoint(new Uri("http://localhost:5000")));

        var execution = Assert.Single(
            result.Devices[0].DataItems,
            item => item.Type == "EXECUTION");

        Assert.Equal("controller-1", execution.ComponentId);
        Assert.Equal("Controller", execution.ComponentType);
    }

    [Fact]
    public async Task DiscoverAsyncSupportsMultipleDevices()
    {
        const string xml = """
            <MTConnectDevices xmlns="urn:mtconnect.org:MTConnectDevices:2.5">
              <Header instanceId="1" version="2.5.0" />
              <Devices>
                <Device id="d1" name="M1" />
                <Device id="d2" name="M2" />
              </Devices>
            </MTConnectDevices>
            """;

        var result = await CreateClient(xml).DiscoverAsync(
            new MtConnectEndpoint(new Uri("http://localhost:5000")));

        Assert.Equal(2, result.Devices.Count);
    }

    [Fact]
    public async Task DiscoverAsyncPropagatesHttpFailure()
    {
        using var httpClient = new HttpClient(
            new StubHandler(HttpStatusCode.ServiceUnavailable, string.Empty));
        var client = new MtConnectDiscoveryClient(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DiscoverAsync(
                new MtConnectEndpoint(new Uri("http://localhost:5000"))));
    }

    [Fact]
    public async Task DiscoverAsyncRejectsDataItemWithoutRequiredId()
    {
        const string xml = """
            <MTConnectDevices xmlns="urn:mtconnect.org:MTConnectDevices:2.5">
              <Devices>
                <Device id="d1"><DataItems><DataItem type="EXECUTION" /></DataItems></Device>
              </Devices>
            </MTConnectDevices>
            """;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateClient(xml).DiscoverAsync(
                new MtConnectEndpoint(new Uri("http://localhost:5000"))));
    }

    private static MtConnectDiscoveryClient CreateClient(string xml)
    {
        var httpClient = new HttpClient(
            new StubHandler(HttpStatusCode.OK, xml));
        return new MtConnectDiscoveryClient(httpClient);
    }

    private static string ProbeXml() => """
        <MTConnectDevices xmlns="urn:mtconnect.org:MTConnectDevices:2.5">
          <Header instanceId="42" version="2.5.0" />
          <Devices>
            <Device id="dev-1" name="CNC-01" uuid="uuid-1">
              <Components>
                <Controller id="controller-1" name="controller">
                  <DataItems>
                    <DataItem id="exec" name="execution" category="EVENT" type="EXECUTION" />
                  </DataItems>
                </Controller>
              </Components>
              <DataItems>
                <DataItem id="avail" name="availability" category="EVENT" type="AVAILABILITY" />
              </DataItems>
            </Device>
          </Devices>
        </MTConnectDevices>
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
