using System.Data;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

/// <summary>Explicit write acceptance on a disposable developer clone only. Never the original RCA database or factory server.</summary>
[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003RestoredContainmentRuntimeAcceptanceTests
{
    private const string Variable = "FACTORYCONNECT_GG003_DISPOSABLE_CONNECTION_STRING";
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private static readonly OperationalMetricProjectionProcessorId Processor = AvailabilityContainmentV1.ProjectionProcessorId(Machine);

    [DisposableRestoreFact]
    public async Task ActualRuntimePublishes534Preserves380WitnessAndVerifiedNestedOeeThenRestartsAndReplays()
    {
        var settings = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)!);
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(settings.DataSource.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("FactoryConnect_GG003_STAGEB_REPLAY_", settings.InitialCatalog, StringComparison.Ordinal);
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using (var database = connection.CreateCommand())
        {
            database.CommandText = "SELECT DB_NAME();";
            Assert.Equal(settings.InitialCatalog, (string)(await database.ExecuteScalarAsync())!);
        }
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), default);
        var exact = await new SqlServerMetricAggregationStore(settings.ConnectionString).ReadExactAsync(
            new MetricAggregationCheckpoint(new MetricAggregationProcessorId($"metric-aggregation:{Machine.Value}"),
                MetricInputStreamId.ForMachine(Machine), new MetricInputPosition(534)), default);
        Assert.NotNull(exact);
        var before = await Fingerprint(connection);
        var store = new SqlServerOperationalMetricProjectionStore(settings.ConnectionString);
        var stream = MetricInputStreamId.ForMachine(Machine);
        Assert.Null(await store.ReadCheckpointAsync(Processor, stream, default));
        await using (var legacy = connection.CreateCommand())
        {
            legacy.CommandText = "SELECT c.Position FROM dbo.OperationalMetricProjectionCheckpoint c JOIN dbo.OperationalMetricProjectionProcessor p ON p.OperationalMetricProjectionProcessorRowId=c.OperationalMetricProjectionProcessorRowId WHERE p.ProcessorKey=@Legacy;";
            legacy.Parameters.AddWithValue("@Legacy", $"operational-metrics:{Machine.Value}:builtins-v1");
            Assert.Equal(524m, (decimal)(await legacy.ExecuteScalarAsync())!);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        await using (var provider = ContainmentProcessorCompositionTests.Build(settings.ConnectionString, Machine))
        {
            var runtime = Assert.Single(provider.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>().Runtimes);
            for (var cycle = 0; cycle < 10_000; cycle++)
            {
                var checkpoint = await store.ReadCheckpointAsync(Processor, stream, timeout.Token);
                if (checkpoint?.SourceRevision.Position.Value >= 534) break;
                Assert.True(await runtime.RunCycleAsync(timeout.Token) > 0, "Runtime stopped before revision 534.");
            }
        }
        var published = await store.ReadCheckpointAsync(Processor, stream, timeout.Token);
        Assert.NotNull(published); Assert.Equal(534UL, published.SourceRevision.Position.Value);
        var shift = new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(new SiteId("c697c1a0-43b1-4aa7-9233-64344788cdf7"),
            new ShiftScheduleAssignmentId("DEWAS-SR-GG003-SCHEDULE-SHIFT-1"), new ShiftId("SHIFT-1"),
            new DateTimeOffset(2026, 10, 16, 0, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.Zero)));
        OperationalMetricEvaluationKey Key(OperationalMetricDefinitionId metric) => new(Machine, shift, metric, OperationalMetricEvaluationContextKey.Unpartitioned);
        var availability = await store.ReadProjectionAsync(Processor, Key(BuiltInOperationalMetricDefinitions.AvailabilityId), timeout.Token);
        Assert.NotNull(availability); Assert.Null(availability.Value);
        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, availability.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, availability.ReasonCode);
        var witness = Assert.IsType<AvailabilityScopeConflictEvidenceV1>(availability.AvailabilityScopeConflictEvidence);
        Assert.Equal(534UL, availability.SourceRevision.Position.Value);
        Assert.Equal(2683.614157m, witness.Numerator.Aggregate.Value);
        Assert.Equal(31, witness.Numerator.Aggregate.InputCount);
        Assert.Equal(2452.013355m, witness.Denominator.Aggregate.Value);
        Assert.Equal(3, witness.Denominator.Aggregate.InputCount);
        Assert.Equal(380UL, witness.Contribution.Position.Value);
        Assert.Equal("05A4224CDC5CBBD1AC23A19F9DEBD3B62AB450EAE0501D00AB06237037411824", witness.Contribution.Fact.Id.Value);
        Assert.Equal(259.637610m, witness.Contribution.Fact.Value);
        Assert.Equal(false, witness.Contribution.Fact.IsPlannedProductionTime);
        Assert.Null(witness.Contribution.Fact.PlannedProductionScheduleAssignmentId);
        var oee = await store.ReadProjectionAsync(Processor, Key(BuiltInOperationalMetricDefinitions.OeeId), timeout.Token);
        Assert.NotNull(oee); Assert.Equal(3, oee.DependencyEvidence.Count);
        Assert.Equal(witness, oee.DependencyEvidence.Single(d => d.DefinitionId == BuiltInOperationalMetricDefinitions.AvailabilityId).Projection.AvailabilityScopeConflictEvidence);
        Assert.NotNull(availability.PublicationMetadata!.ConflictWitnessId); Assert.Null(oee.PublicationMetadata!.ConflictWitnessId);

        // Bound only the replay probe at 534: normal Edge processing would legitimately select later revisions.
        // This is the same runtime/source/publisher, with revision discovery capped for this acceptance probe.
        var aggregation = new SqlServerMetricAggregationStore(settings.ConnectionString);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var replaySource = new CoherentOperationalMetricEvaluationBatchSource(catalog, new ReplayRevisionReader(aggregation), aggregation,
            published.SourceRevision.ProcessorId, stream, OperationalMetricEvaluationContextKey.Unpartitioned, useAvailabilityContainmentV1: true);
        var restart = new OperationalMetricProjectionProcessingRuntime(Processor, published.SourceRevision.ProcessorId, stream, replaySource,
            new OperationalMetricProjectionFactory(catalog, Processor), store, containmentPublisher: new SqlServerOperationalMetricContainmentPublisher(settings.ConnectionString));
        var complete = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null);
        Assert.Equal(0, await restart.RunCycleAsync(timeout.Token)); Assert.Equal(0, await restart.RunCycleAsync(timeout.Token));
        Assert.Equal(complete, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null));
        Assert.Equal(before, await Fingerprint(connection));
        // A canonical but forged runtime result must conflict, not replace the accepted publication.
        var forged = new OperationalMetricProjection(Processor, oee.Key, OperationalMetricEvaluationStatus.Calculated, 0.5m, oee.Unit,
            null, null, oee.SourceRevision, oee.OperandEvidence, oee.DependencyEvidence, evaluationPolicyId: AvailabilityContainmentV1.PolicyId);
        var forgedCheckpoint = new OperationalMetricProjectionCheckpoint(Processor, published.SourceRevision, new OperationalMetricProjectionBatchManifest([forged.Key]));
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => new SqlServerOperationalMetricContainmentPublisher(settings.ConnectionString)
            .PublishAsync(published, forgedCheckpoint, [forged], timeout.Token).AsTask());
        Assert.Equal(complete, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null));
    }

    private sealed class ReplayRevisionReader(IMetricAggregationRevisionReader reader) : IMetricAggregationRevisionReader
    {
        public async ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(MetricAggregationProcessorId processorId, MetricInputStreamId streamId,
            MetricAggregationCheckpoint? afterRevision, CancellationToken cancellationToken)
        {
            var next = await reader.ReadNextAsync(processorId, streamId, afterRevision, cancellationToken);
            return next?.Revision.Position.Value > 534 ? null : next;
        }
        public ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(MetricAggregationCheckpoint revision, CancellationToken cancellationToken) =>
            reader.ReadExactAsync(revision, cancellationToken);
    }
    private static async Task<string> Fingerprint(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @S nvarchar(max)=(SELECT
            (SELECT * FROM dbo.MetricInputFact ORDER BY MetricInputFactRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS Facts,
            (SELECT * FROM dbo.MetricAggregationCheckpoint ORDER BY MetricAggregationProcessorRowId FOR JSON PATH) AS AggregationCheckpoint,
            (SELECT * FROM dbo.MetricAggregationContribution ORDER BY MetricAggregationProcessorRowId,Position FOR JSON PATH) AS Contributions,
            (SELECT p.* FROM dbo.OperationalMetricProjectionProcessor p WHERE p.PublicationFormatVersion=1 ORDER BY p.OperationalMetricProjectionProcessorRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS LegacyProcessor,
            (SELECT c.* FROM dbo.OperationalMetricProjectionCheckpoint c JOIN dbo.OperationalMetricProjectionProcessor p ON p.OperationalMetricProjectionProcessorRowId=c.OperationalMetricProjectionProcessorRowId WHERE p.PublicationFormatVersion=1 ORDER BY c.OperationalMetricProjectionProcessorRowId FOR JSON PATH) AS LegacyCheckpoint,
            (SELECT r.* FROM dbo.OperationalMetricProjection r WHERE r.PublicationFormatVersion=1 ORDER BY r.OperationalMetricProjectionRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS LegacyRows,
            (SELECT e.* FROM dbo.OperationalMetricProjectionEvidence e JOIN dbo.OperationalMetricProjection r ON r.OperationalMetricProjectionRowId=e.OperationalMetricProjectionRowId WHERE r.PublicationFormatVersion=1 ORDER BY e.OperationalMetricProjectionEvidenceRowId FOR JSON PATH,INCLUDE_NULL_VALUES) AS LegacyEvidence,
            (SELECT m.* FROM dbo.OperationalMetricProjectionManifest m JOIN dbo.OperationalMetricProjectionProcessor p ON p.OperationalMetricProjectionProcessorRowId=m.OperationalMetricProjectionProcessorRowId WHERE p.PublicationFormatVersion=1 ORDER BY m.OperationalMetricProjectionProcessorRowId,m.OperationalMetricProjectionRowId FOR JSON PATH) AS LegacyManifest
            FOR JSON PATH); SELECT @S;
            """;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((string)(await command.ExecuteScalarAsync())!)));
    }
    public sealed class DisposableRestoreFactAttribute : FactAttribute
    {
        public DisposableRestoreFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = "Requires an explicitly supplied disposable Windows LocalDB clone; never production or the original RCA restore.";
        }
    }
}
