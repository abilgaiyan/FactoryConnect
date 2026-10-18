using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

/// <summary>Opt-in read-only acceptance against the developer's existing LocalDB restore.</summary>
[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003RestoredHistoricalAcceptanceTests
{
    private const string ConnectionVariable = "FACTORYCONNECT_GG003_RESTORED_CONNECTION_STRING";
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private static readonly SiteId Site = new("c697c1a0-43b1-4aa7-9233-64344788cdf7");
    private static readonly MetricAggregationProcessorId Processor = new($"metric-aggregation:{Machine.Value}");
    private static readonly MetricInputStreamId Stream = MetricInputStreamId.ForMachine(Machine);
    private static readonly MetricAggregationCheckpoint Revision = new(Processor, Stream, new MetricInputPosition(534));

    [RestoredGg003Fact]
    public async Task RestoredShift534ReturnsUnavailableWithOriginalWitness()
    {
        var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException("Restored connection not supplied."));
        Assert.True(connection.DataSource.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase),
            "This probe accepts only a developer LocalDB instance.");
        Assert.Equal("FactoryConnect_GG003_RCA_20261016", connection.InitialCatalog);
        connection.ApplicationIntent = ApplicationIntent.ReadOnly;
        var store = new SqlServerMetricAggregationStore(connection.ConnectionString);
        var shift = new ShiftOccurrenceId(Site,
            new ShiftScheduleAssignmentId("DEWAS-SR-GG003-SCHEDULE-SHIFT-1"), new ShiftId("SHIFT-1"),
            new DateTimeOffset(2026, 10, 16, 0, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.Zero));
        var key = new OperationalMetricEvaluationKey(Machine, new OperationalMetricPeriodId.Shift(shift),
            BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned);
        var operands = BuiltInOperationalMetricDefinitions.All
            .Single(definition => definition.Id == BuiltInOperationalMetricDefinitions.AvailabilityId).Operands;
        var request = new OperationalMetricComponentSnapshotRequest(key, Processor, operands,
            includeAvailabilityScopeConflictEvidence: true);
        var checkpointBefore = await store.ReadCheckpointAsync(Processor, Stream, CancellationToken.None);
        Assert.NotNull(checkpointBefore);
        Assert.True(checkpointBefore.Position.Value >= 534);
        var snapshot = await store.ReadAtRevisionAsync(request, Revision, CancellationToken.None);
        Assert.Equal(Revision, snapshot.Revision);
        var running = snapshot.Components.Single(component => component.SourceIdentity.ComponentKey == MetricInputFactKeys.RunningDuration);
        var planned = snapshot.Components.Single(component => component.SourceIdentity.ComponentKey == MetricInputFactKeys.PlannedProductionDuration);
        Assert.Equal(2683.614157m, running.Aggregate.Value);
        Assert.Equal(31, running.Aggregate.InputCount);
        Assert.Equal(2452.013355m, planned.Aggregate.Value);
        Assert.Equal(3, planned.Aggregate.InputCount);
        var witness = Assert.IsType<AvailabilityScopeConflictEvidenceV1>(snapshot.AvailabilityScopeConflictEvidence);
        Assert.Equal(Revision, witness.SourceRevision);
        Assert.Equal(380UL, witness.Contribution.Position.Value);
        Assert.Equal(new MetricInputFactId("05A4224CDC5CBBD1AC23A19F9DEBD3B62AB450EAE0501D00AB06237037411824"), witness.Contribution.Fact.Id);
        Assert.Equal(259.637610m, witness.Contribution.Fact.Value);
        Assert.Equal(false, witness.Contribution.Fact.IsPlannedProductionTime);
        Assert.Null(witness.Contribution.Fact.PlannedProductionScheduleAssignmentId);
        var evaluator = new OperationalMetricEvaluator(
            new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All),
            new PinnedReader(store), Processor, useAvailabilityContainmentV1: true);
        var result = await evaluator.EvaluateAsync(key, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, result.ReasonCode);
        Assert.Null(result.Value);
        Assert.Equal(Revision, result.SourceRevision);
        Assert.Equal(witness, result.AvailabilityScopeConflictEvidence);
        Assert.Equal(checkpointBefore, await store.ReadCheckpointAsync(Processor, Stream, CancellationToken.None));
    }

    private sealed class PinnedReader(IRevisionedOperationalMetricComponentSnapshotReader store)
        : IOperationalMetricComponentSnapshotReader
    {
        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request, CancellationToken cancellationToken) =>
            store.ReadAtRevisionAsync(request, Revision, cancellationToken);
    }

    public sealed class RestoredGg003FactAttribute : FactAttribute
    {
        public RestoredGg003FactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = "Opt-in LocalDB restore acceptance; connection environment variable not supplied.";
        }
    }
}
