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
    public async Task RestartedCaughtUpPollingAndReportingCoexistWithHeldSharedGate()
    {
        var (_, cut) = await new SqlContainmentHistoricalResultIntegrationTests(fixture)
            .CreateArchiveTestEvidenceAsync(canonicalProcessor: true);
        var machine = cut.StreamId.MachineId;
        var processor = AvailabilityContainmentV1.ProjectionProcessorId(machine);
        await using (var initial = ContainmentProcessorCompositionTests.Build(fixture.ConnectionString, machine))
        {
            var runtime = Assert.Single(initial.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>().Runtimes);
            Assert.Equal(10, await runtime.RunPollingCycleAsync());
        }
        var spans = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("processor.id"), processor.Value)) spans.Enqueue(activity);
            }
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(timeout.Token);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, transaction, processor,
            OperationalMetricProjectionProcessorGateMode.Shared, timeout.Token);
        await using var restarted = ContainmentProcessorCompositionTests.Build(fixture.ConnectionString, machine);
        var pollingRuntime = Assert.Single(restarted.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>().Runtimes);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Poll()
        {
            await start.Task;
            for (var poll = 0; poll < 10; poll++)
                Assert.Equal(0, await pollingRuntime.RunPollingCycleAsync(timeout.Token));
        }
        async Task Report()
        {
            await start.Task;
            var checkpoint = await new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString)
                .ReadCheckpointAsync(processor, cut.StreamId, timeout.Token);
            Assert.NotNull(checkpoint);
            Assert.Equal(cut, checkpoint.SourceRevision);
        }
        var polling = Poll(); var reporting = Report();
        start.SetResult();
        await Task.WhenAll(polling, reporting);
        Assert.DoesNotContain(spans, a => a.OperationName == "processor.gate.acquire" &&
            Equals(a.GetTagItem("gate.mode"), "Exclusive"));
        Assert.Contains(spans, a => a.OperationName == "containment.publication.verify" &&
            Equals(a.GetTagItem("verification.completed"), true));
        await transaction.RollbackAsync(timeout.Token);
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
