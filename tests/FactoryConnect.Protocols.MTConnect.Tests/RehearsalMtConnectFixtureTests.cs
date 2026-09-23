using System.Net;
using FactoryConnect.Abstractions;
using FactoryConnect.Rehearsal.MTConnectFixture;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class RehearsalMtConnectFixtureTests
{
    [Fact]
    public void TopologyIsDeterministicAndContainsExactlySevenUniqueMachines()
    {
        var machines = RehearsalFixtureTopology.Machines;

        Assert.Equal(7, machines.Count);
        Assert.Equal(7, machines.Select(static machine => machine.MachineId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(7, machines.Select(static machine => machine.DeviceKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(7, machines.Select(static machine => machine.BasePath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, machines.Count(static machine => machine.ProductionLineId == "LINE-1"));
        Assert.Equal(2, machines.Count(static machine => machine.ProductionLineId == "LINE-2"));
    }

    [Fact]
    public async Task ProductionClientsAcceptProbeCurrentAndSampleForEveryMachine()
    {
        var state = new RehearsalMtConnectState();
        using var httpClient = new HttpClient(new FixtureHandler(state))
        {
            BaseAddress = new Uri("http://fixture.invalid")
        };
        var discovery = new MtConnectDiscoveryClient(httpClient);
        var current = new MtConnectCurrentClient(httpClient);
        var sample = new MtConnectSampleClient(httpClient);

        foreach (var machine in RehearsalFixtureTopology.Machines)
        {
            var endpoint = new MtConnectEndpoint(new Uri(httpClient.BaseAddress, machine.BasePath));
            var machineId = MachineId.Parse(machine.MachineId);

            var discovered = await discovery.DiscoverAsync(endpoint);
            Assert.Equal(RehearsalFixtureTopology.InstanceId.ToString(), discovered.AgentInstanceId);
            var device = Assert.Single(discovered.Devices);
            Assert.Equal(machine.DeviceKey, device.Name);
            Assert.Equal(machine.DeviceUuid, device.Uuid);

            var currentResult = await current.AcquireResultAsync(
                endpoint,
                machineId,
                machine.DeviceKey);

            Assert.Equal(RehearsalFixtureTopology.InstanceId, currentResult.InstanceId);
            Assert.NotEmpty(currentResult.Observations);

            var sampleResult = await sample.AcquireAsync(
                endpoint,
                machineId,
                machine.DeviceKey,
                currentResult.NextSequence);

            Assert.Equal(RehearsalFixtureTopology.InstanceId, sampleResult.InstanceId);
            Assert.True(sampleResult.NextSequence > currentResult.NextSequence);
            Assert.All(
                sampleResult.Observations,
                observation => Assert.Equal(machineId, observation.Observation.MachineId));
        }
    }

    [Fact]
    public async Task SampleSequenceAdvancesWithoutChangingInstanceIdentity()
    {
        var state = new RehearsalMtConnectState();
        using var httpClient = new HttpClient(new FixtureHandler(state));
        var sample = new MtConnectSampleClient(httpClient);
        var machine = RehearsalFixtureTopology.Machines[0];
        var endpoint = new MtConnectEndpoint(new Uri("http://fixture.invalid" + machine.BasePath));
        var machineId = MachineId.Parse(machine.MachineId);

        var first = await sample.AcquireAsync(endpoint, machineId, machine.DeviceKey, 1);
        var second = await sample.AcquireAsync(endpoint, machineId, machine.DeviceKey, first.NextSequence);

        Assert.Equal(RehearsalFixtureTopology.InstanceId, first.InstanceId);
        Assert.Equal(first.InstanceId, second.InstanceId);
        Assert.True(second.NextSequence > first.NextSequence);
        Assert.True(second.Observations.Min(static observation => observation.Sequence) >= first.NextSequence);
    }

    private sealed class FixtureHandler(RehearsalMtConnectState state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var machine = RehearsalFixtureTopology.Machines.SingleOrDefault(
                candidate => path.StartsWith(candidate.BasePath, StringComparison.Ordinal));

            if (machine is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            string? xml = null;
            if (path.EndsWith("/probe", StringComparison.Ordinal))
            {
                xml = state.GetProbeDocument(machine);
            }
            else if (path.EndsWith("/current", StringComparison.Ordinal))
            {
                xml = state.GetCurrentDocument(machine);
            }
            else if (path.EndsWith("/sample", StringComparison.Ordinal))
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                if (!ulong.TryParse(query["from"], out var from))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                }

                xml = state.GetSampleDocument(machine, from);
            }

            var response = new HttpResponseMessage(
                xml is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(xml ?? string.Empty)
            };
            response.Content.Headers.ContentType = new("application/xml");
            return Task.FromResult(response);
        }
    }
}
