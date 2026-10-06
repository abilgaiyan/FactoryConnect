using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryConnect.HistoricalRecovery;
using Xunit;

namespace FactoryConnect.HistoricalRecovery.Tests;

public sealed class HistoricalRecoveryFingerprintTests
{
    [Fact]
    public void CanonicalPropertyOrderAndDecimalScaleDoNotChangeFingerprint()
    {
        using var a = JsonDocument.Parse("{\"b\":1.00,\"a\":2}");
        using var b = JsonDocument.Parse("{\"a\":2.0,\"b\":1}");
        Assert.Equal(RecoveryFingerprint.Compute(a.RootElement), RecoveryFingerprint.Compute(b.RootElement));
    }
    [Theory]
    [InlineData("Target")]
    [InlineData("HistoricalEvidence")]
    [InlineData("DefinitionAuthority")]
    [InlineData("Definitions")]
    [InlineData("Evaluation1")]
    [InlineData("Projections")]
    public async Task EveryAuthorityGroupIsFingerprintBound(string group)
    {
        var preview = await HistoricalRecoveryApplyTests.CreateRunner().PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        var node = JsonNode.Parse(preview.CanonicalPayload.GetRawText())!.AsObject();
        node[group] = "changed";
        Assert.NotEqual(preview.Fingerprint, RecoveryFingerprint.Compute(RecoveryJson.Element(node)));
    }
    [Fact]
    public async Task DirectAndRecursiveEvidenceAreBound()
    {
        var preview = await HistoricalRecoveryApplyTests.CreateRunner().PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        var node = JsonNode.Parse(preview.CanonicalPayload.GetRawText())!;
        var projections = node["Projections"]!.AsArray();
        var availability = projections.Single(p => p!["Key"]!["DefinitionId"]!["MetricKey"]!.GetValue<string>() == "availability")!;
        availability["OperandEvidence"] = new JsonArray();
        Assert.NotEqual(preview.Fingerprint, RecoveryFingerprint.Compute(RecoveryJson.Element(node)));
        node = JsonNode.Parse(preview.CanonicalPayload.GetRawText())!;
        var oee = node["Projections"]!.AsArray().Single(p => p!["Key"]!["DefinitionId"]!["MetricKey"]!.GetValue<string>() == "oee")!;
        oee["DependencyEvidence"]![0]!["Projection"]!["OperandEvidence"] = new JsonArray();
        Assert.NotEqual(preview.Fingerprint, RecoveryFingerprint.Compute(RecoveryJson.Element(node)));
    }
    [Fact]
    public async Task ContributionIdentityAndFactContentAreBound()
    {
        var preview = await HistoricalRecoveryApplyTests.CreateRunner().PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        foreach (var field in new[] { "PrefixMembership", "PrefixFacts", "Snapshot", "Transition", "ReferenceTimeCut" })
        {
            var node = JsonNode.Parse(preview.CanonicalPayload.GetRawText())!;
            node["HistoricalEvidence"]![field] = "changed";
            Assert.NotEqual(preview.Fingerprint, RecoveryFingerprint.Compute(RecoveryJson.Element(node)));
        }
    }
}
