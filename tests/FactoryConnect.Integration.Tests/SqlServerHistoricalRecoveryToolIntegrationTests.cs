using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.HistoricalRecovery;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerHistoricalRecoveryToolIntegrationTests(SqlServerTestDatabaseFixture fixture)
    : IClassFixture<SqlServerTestDatabaseFixture>
{
    [Fact]
    public async Task ReadsHistoricalDayTwiceWithoutChangingDurableRowsOrLiveReplayAuthority()
    {
        var (target, inputs) = await SeedAsync();
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var projectionProcessor = new OperationalMetricProjectionProcessorId(
            $"operational-metrics:{target.Revision.StreamId.MachineId.Value:D}:builtins-v1");
        var source = new CoherentOperationalMetricEvaluationBatchSource(catalog,
            new SqlServerMetricAggregationStore(fixture.ConnectionString),
            new SqlServerMetricAggregationStore(fixture.ConnectionString),
            target.Revision.ProcessorId, target.Revision.StreamId, OperationalMetricEvaluationContextKey.Unpartitioned);
        var batch = await source.ReadAsync(new OperationalMetricEvaluationBatchRequest(
            target.Revision.ProcessorId, target.Revision.StreamId, null), CancellationToken.None);
        Assert.NotNull(batch);
        var factory = new OperationalMetricProjectionFactory(catalog, projectionProcessor);
        var projections = batch.Evaluations.Where(e => e.Key.PeriodId != target.Period).Select(factory.Create).ToArray();
        await new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString).CommitAsync(
            new OperationalMetricProjectionCommit(projectionProcessor, null,
                new OperationalMetricProjectionCheckpoint(projectionProcessor, target.Revision,
                    new OperationalMetricProjectionBatchManifest(projections.Select(value => value.Key))), projections),
            CancellationToken.None);

        var before = await ReadDatabaseEvidenceAsync(target);
        using var reader = new SqlHistoricalRecoveryReader(fixture.ConnectionString);
        var record = await new HistoricalReconstruction(reader).RunAsync(target);
        var after = await ReadDatabaseEvidenceAsync(target);

        Assert.Equal(before, after);
        Assert.Equal(inputs.Count, record.Capture.PrefixFacts.Count);
        Assert.Equal(4, record.SelectedPeriodFacts.Count);
        Assert.Equal(5, record.Evaluation1.Count);
        Assert.Equal(0.5m, record.Evaluation1.Single(value => value.Key.DefinitionId.MetricKey == "availability").Value);
        Assert.Equal("PASS", record.ExactOutcomeAndRecursiveEvidenceEquivalence);
        Assert.All(record.Evaluation1, value => Assert.Equal(target.Period, value.Key.PeriodId));
        Assert.Equal(0, record.Capture.Transition.CompletedReferenceTimeRevision!.Value.Value);
        Assert.Contains("Manifest", record.Capture.LiveAuthority.ToString(), StringComparison.Ordinal);
        var runner = new HistoricalRecoveryRunner(reader, reader,
            new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString), new TestDeployment());
        var preview = await runner.PreviewAsync(target);
        Assert.Equal(RecoveryClassification.Recoverable, preview.Classification);
        Assert.Equal(after, await ReadDatabaseEvidenceAsync(target));
        var applied = await runner.ApplyAsync(target, RecoveryJson.Element(preview));
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Recovered, applied.Result.Outcome);
        Assert.True(applied.TargetExactlyEquivalentAfter);
        Assert.Equal(preview.Authority.Checkpoint, applied.AuthorityAfter.Checkpoint);
        Assert.Equal(preview.Authority.Manifest, applied.AuthorityAfter.Manifest);
        var persisted = await ReadDatabaseEvidenceAsync(target);
        var retry = await runner.ApplyAsync(target, RecoveryJson.Element(preview));
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Equivalent, retry.Result.Outcome);
        Assert.Equal(persisted, await ReadDatabaseEvidenceAsync(target));
    }

    private sealed class TestDeployment : IRecoveryDeploymentVerifier
    {
        public Task<System.Text.Json.JsonElement> VerifyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(RecoveryJson.Element(new { Status = "Verified", Scope = "Disposable SQL fixture only" }));
    }

    private (RecoveryTarget Target, IReadOnlyList<PositionedMetricInputFact> Inputs)? _seed;

    private async Task<(RecoveryTarget Target, IReadOnlyList<PositionedMetricInputFact> Inputs)> SeedAsync()
    {
        if (_seed is { } existing) return existing;
        // Test fixture setup owns all writes; the diagnostic itself receives read calls only.
        var machine = new MachineId(Guid.Parse(RecoveryTargets.Machine));
        var stream = new MetricInputStreamId(machine, "metric-inputs");
        var processor = new MetricAggregationProcessorId($"metric-aggregation:{RecoveryTargets.Machine}");
        var site = new SiteId("CAMPUS-1");
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 4));
        var starts = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var shift = new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId("test-schedule"),
            new ShiftId("SHIFT-1"), starts, starts.AddHours(8));
        var store = new SqlServerMetricInputStore(fixture.ConnectionString);
        var facts = new List<PositionedMetricInputFact>();
        var specs = new (string Key, decimal Value)[]
        {
            (MetricInputFactKeys.RunningDuration, 100m),
            (MetricInputFactKeys.PlannedProductionDuration, 200m),
            (MetricInputFactKeys.ScheduledDuration, 400m),
            (MetricInputFactKeys.StoppedDuration, 300m),
        };
        foreach (var spec in specs)
        {
            facts.Add(await store.AppendAsync(new DurableMetricInputAppend(stream, new DurableMetricInputFact
            {
                Id = new MetricInputFactId($"diag-{Guid.NewGuid():N}"), Key = spec.Key, Value = spec.Value,
                Unit = MetricInputFactUnits.Seconds, StartsAtUtc = starts, EndsAtUtc = starts.AddMinutes(10),
                CompanyId = new CompanyId("company"), SiteId = site, MachineId = machine,
                ShiftId = shift.ShiftId, ShiftScheduleAssignmentId = shift.ShiftScheduleAssignmentId,
            }, shift, day), CancellationToken.None));
        }

        var neighbor = new ProductionDayId(site, day.BusinessDate.AddDays(1));
        var neighborShift = new ShiftOccurrenceId(site, shift.ShiftScheduleAssignmentId, shift.ShiftId,
            starts.AddDays(1), starts.AddDays(1).AddHours(8));
        facts.Add(await store.AppendAsync(new DurableMetricInputAppend(stream, facts[0].Fact with
        {
            Id = new MetricInputFactId($"diag-{Guid.NewGuid():N}"), StartsAtUtc = neighborShift.StartsAtUtc,
            EndsAtUtc = neighborShift.StartsAtUtc.AddMinutes(10),
        }, neighborShift, neighbor), CancellationToken.None));
        // Disposable fixture deliberately uses a sparse prefix to exercise the fixed source cut.
        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.MetricInputFact SET Position=1211 WHERE Position=5 AND MachineId=@Machine";
            command.Parameters.AddWithValue("@Machine", machine.Value);
            await command.ExecuteNonQueryAsync();
        }
        facts[^1] = new PositionedMetricInputFact(stream, new MetricInputPosition(1211), facts[^1].Fact, neighborShift, neighbor);
        var revision = new MetricAggregationCheckpoint(processor, stream, facts[^1].Position);
        await new SqlServerMetricAggregationStore(fixture.ConnectionString).CommitAsync(
            new MetricAggregationCommit(processor, null, revision, facts), CancellationToken.None);
        await new SqlServerProductionReferenceTimeConvergenceCoordinator(fixture.ConnectionString)
            .ConvergeAsync(revision, machine, null, CancellationToken.None);
        _seed = (new RecoveryTarget(revision, day), facts);
        return _seed.Value;
    }

    private async Task<string> ReadDatabaseEvidenceAsync(RecoveryTarget target)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT * FROM dbo.MetricInputFact WHERE MachineId = @Machine ORDER BY Position FOR JSON PATH) AS Facts,
              (SELECT c.* FROM dbo.MetricAggregationContribution c JOIN dbo.MetricAggregationProcessor p
                 ON p.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
               WHERE p.ProcessorKey = @Processor ORDER BY c.Position FOR JSON PATH) AS Contributions,
              (SELECT c.* FROM dbo.MetricAggregationCheckpoint c JOIN dbo.MetricAggregationProcessor p
                 ON p.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
               WHERE p.ProcessorKey = @Processor FOR JSON PATH) AS AggregationCheckpoint,
              (SELECT t.* FROM dbo.ProductionReferenceTimePublicationTransition t JOIN dbo.MetricAggregationProcessor p
                 ON p.MetricAggregationProcessorRowId = t.MetricAggregationProcessorRowId
               WHERE p.ProcessorKey = @Processor ORDER BY t.TargetMetricAggregationPosition FOR JSON PATH) AS Transitions,
              (SELECT v.* FROM dbo.OperationalMetricProjection v WHERE v.MachineId = @Machine
               ORDER BY v.OperationalMetricProjectionRowId FOR JSON PATH) AS Projections,
              (SELECT e.* FROM dbo.OperationalMetricProjectionEvidence e JOIN dbo.OperationalMetricProjection v
                 ON v.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId
               WHERE v.MachineId = @Machine ORDER BY e.OperationalMetricProjectionEvidenceRowId FOR JSON PATH) AS Evidence,
              (SELECT c.* FROM dbo.OperationalMetricProjectionCheckpoint c JOIN dbo.OperationalMetricProjectionProcessor p
                 ON p.OperationalMetricProjectionProcessorRowId = c.OperationalMetricProjectionProcessorRowId
               JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId = p.MetricInputStreamRowId
               WHERE s.MachineId = @Machine FOR JSON PATH) AS ProjectionCheckpoint,
              (SELECT m.* FROM dbo.OperationalMetricProjectionManifest m JOIN dbo.OperationalMetricProjection v
                 ON v.OperationalMetricProjectionRowId = m.OperationalMetricProjectionRowId
               WHERE v.MachineId = @Machine ORDER BY m.OperationalMetricProjectionRowId FOR JSON PATH) AS Manifest
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
            """;
        command.Parameters.AddWithValue("@Machine", target.Revision.StreamId.MachineId.Value);
        command.Parameters.AddWithValue("@Processor", target.Revision.ProcessorId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
