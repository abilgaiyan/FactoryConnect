using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectAcquisitionOptionsTests
{
    [Fact]
    public void ConstructorPreservesConfiguration()
    {
        var endpoint = new MtConnectEndpoint(
            new Uri("http://localhost:5000"));
        var machineId = MachineId.New();
        var interval = TimeSpan.FromSeconds(1);

        var options = new MtConnectAcquisitionOptions(
            endpoint,
            machineId,
            "CNC-01",
            101,
            interval);

        Assert.Same(endpoint, options.Endpoint);
        Assert.Equal(machineId, options.MachineId);
        Assert.Equal("CNC-01", options.DeviceKey);
        Assert.Equal(101UL, options.FromSequence);
        Assert.Equal(interval, options.PollingInterval);
    }

    [Fact]
    public void ConstructorRejectsEmptyMachineId()
    {
        Assert.Throws<ArgumentException>(
            () => new MtConnectAcquisitionOptions(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                default,
                "CNC-01",
                1,
                TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ConstructorRejectsNonPositivePollingInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MtConnectAcquisitionOptions(
                new MtConnectEndpoint(
                    new Uri("http://localhost:5000")),
                MachineId.New(),
                "CNC-01",
                1,
                TimeSpan.Zero));
    }
}
