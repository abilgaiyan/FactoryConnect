using System.Net;
using System.Text.Json;
using FactoryConnect.Api.Commissioning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace FactoryConnect.Integration.Tests;

public sealed class CommissioningVisibilityTests
{
    [Theory]
    [InlineData(false, "127.0.0.1", false, false)]
    [InlineData(true, "10.0.0.1", false, false)]
    [InlineData(true, "127.0.0.1", true, false)]
    [InlineData(true, "127.0.0.1", false, true)]
    [InlineData(true, "::1", false, true)]
    public void AccessRequiresExplicitLocalUnforwardedReview(bool enabled, string ip, bool forwarded, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (forwarded) context.Request.Headers["X-Forwarded-For"] = "10.0.0.1";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Commissioning:Enabled"] = enabled.ToString() }).Build();
        Assert.Equal(expected, CommissioningAccess.Allows(context, configuration));
    }

    [Fact]
    public void ClosedProjectionPreservesMachineScopesAndWithholdsSecrets()
    {
        using var document = JsonDocument.Parse("""
            {
              "PersistenceProviders":{"SqlServer":{"ConnectionString":"SECRET-CONNECTION"}},
              "MTConnect":{"Machines":[{"MachineId":"GG003","DeviceKey":"CNC","BaseUri":"https://user:SECRET@host"}]},
              "ObservationProcessing":{"Streams":[{"MachineId":"MOXA","StreamKey":"moxa","MoxaStatusNormalization":{"Source":"mtconnect","Address":"status"},"Mappings":[{"Source":"mtconnect","Address":"status","Type":"Enumeration","SignalKey":"state.operational","Password":"SECRET-MAPPING"}]}]},
              "ProductionProcessing":{"Machines":[{"MachineId":"GG003","ActivityStreamKey":"gg003"},{"MachineId":"MOXA","ActivityStreamKey":"moxa","ProductionStartsAt":"2026-10-25T02:00:00Z","Token":"SECRET-TOKEN"}]}
            }
            """);
        var fields = CommissioningSnapshotReader.Project(document.RootElement);
        var json = JsonSerializer.Serialize(fields);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseUri", json, StringComparison.Ordinal);
        Assert.Contains(fields, field => field.Path == "ProductionProcessing:Machines:1:MachineId" && field.Value == "MOXA");
        Assert.Contains(fields, field => field.Path == "ObservationProcessing:Streams:0:Mappings:0:Type" && field.Value == "Enumeration");
        Assert.DoesNotContain(fields, field => field.Path == "ProductionProcessing:Machines:0:ProductionStartsAt");
    }

    [Theory]
    [InlineData("Server=db;Password=p")]
    [InlineData("https://user:password@host")]
    [InlineData("Bearer secret-token")]
    public void SecretShapedAllowedValuesAreRedacted(string value) =>
        Assert.Equal("[redacted]", CommissioningSnapshotReader.PublicValue(value));

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"MTConnect\":{},\"mtconnect\":{}}")]
    public void AmbiguousCaptureFailsClosed(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => CommissioningSnapshotReader.Project(document.RootElement));
    }

    [Fact]
    public async Task MissingSourcesRemainUnknownWithNoFabricatedProgress()
    {
        using var reader = new CommissioningSnapshotReader(new ConfigurationBuilder().Build());
        var snapshot = await reader.ReadAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal("unavailable", snapshot.Configuration.Status);
        Assert.Equal("unavailable", snapshot.Durable.Status);
        Assert.Empty(snapshot.Durable.Processors);
        Assert.Null(snapshot.Durable.ReadCompletedAt);
        Assert.Equal("unknown/unverified", snapshot.RuntimeActivation);
    }
}
