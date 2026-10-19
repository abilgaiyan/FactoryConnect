using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentProcessorCompositionIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Fact]
    public async Task RealComposedRuntimePublishesRestartsAndReplaysWithoutLegacyMutation()
    {
        var (_, cut) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync(canonicalProcessor: true);
        var machine = cut.StreamId.MachineId;
        await using (var provider = ContainmentProcessorCompositionTests.Build(fixture.ConnectionString, machine))
        {
            var runtime = Assert.Single(provider.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>().Runtimes);
            Assert.Equal(10, await runtime.RunCycleAsync());
        }
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null);
        await using (var provider = ContainmentProcessorCompositionTests.Build(fixture.ConnectionString, machine))
        {
            var runtime = Assert.Single(provider.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>().Runtimes);
            Assert.Equal(0, await runtime.RunCycleAsync()); Assert.Equal(0, await runtime.RunCycleAsync());
        }
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null));
        var processor = AvailabilityContainmentV1.ProjectionProcessorId(machine);
        var checkpoint = await new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString).ReadCheckpointAsync(processor, cut.StreamId, default);
        Assert.NotNull(checkpoint); Assert.Equal(cut, checkpoint.SourceRevision); Assert.Equal(10, checkpoint.BatchManifest.ProjectionKeys.Count);
        foreach (var key in checkpoint.BatchManifest.ProjectionKeys.Where(k => k.DefinitionId == BuiltInOperationalMetricDefinitions.AvailabilityId))
        {
            var result = await new SqlServerOperationalMetricProjectionQueryReader(fixture.ConnectionString).ReadDetailAsync(processor, key, default);
            Assert.NotNull(result); Assert.Null(result.Value); Assert.NotNull(result.AvailabilityScopeConflictEvidence);
            Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, result.ReasonCode);
            Assert.Throws<NotSupportedException>(() => new OperationalMetricProjectionCommit(processor, null, checkpoint, [result]));
        }
        await using var check = connection.CreateCommand(); check.CommandText = "SELECT COUNT(*) FROM dbo.OperationalMetricProjectionProcessor WHERE PublicationFormatVersion=1;";
        Assert.Equal(0, (int)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ForgedRuntimeResultCannotBeSubstitutedByReconstructionOrAdvanceCheckpoint()
    {
        var (envelopes, cut) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync(canonicalProcessor: true);
        var claim = OperationalMetricDependencySnapshotV2Codec.Decode(envelopes[1].Span);
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        OperationalMetricProjection proof;
        await using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            proof = (await SqlContainmentHistoricalResultValidator.ReconstructVerifiedAsync(connection, transaction, claim.Key, cut, default)).Projection;
        var children = proof.DependencyEvidence.ToArray(); var quality = children[2].Projection;
        children[2] = new(children[2].OperandName, children[2].DefinitionId, new(quality.ProcessorId, quality.Key, quality.Status,
            0.5m, quality.Unit, quality.ReasonCode, quality.ReasonOperandName, quality.SourceRevision, quality.OperandEvidence,
            quality.DependencyEvidence, quality.AvailabilityScopeConflictEvidence, quality.EvaluationPolicyId));
        var forged = new OperationalMetricProjection(proof.ProcessorId, proof.Key, proof.Status, proof.Value, proof.Unit, proof.ReasonCode,
            proof.ReasonOperandName, cut, proof.OperandEvidence, children, proof.AvailabilityScopeConflictEvidence, proof.EvaluationPolicyId);
        var proposed = new OperationalMetricProjectionCheckpoint(proof.ProcessorId, cut, new OperationalMetricProjectionBatchManifest([proof.Key]));
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => new SqlServerOperationalMetricContainmentPublisher(fixture.ConnectionString)
            .PublishAsync(null, proposed, [forged], default).AsTask());
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null));
    }
}
