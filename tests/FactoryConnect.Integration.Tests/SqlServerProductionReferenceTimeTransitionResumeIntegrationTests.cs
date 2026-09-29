using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerProductionReferenceTimeTransitionResumeIntegrationTests(
    SqlServerTestDatabaseFixture fixture) : IClassFixture<SqlServerTestDatabaseFixture>
{
    [Fact]
    public async Task ConcurrentWorkersConvergeOnOneClaimAndExactSourceSet()
    {
        var machine = new MachineId(Guid.NewGuid());
        var processor = new MetricAggregationProcessorId($"reference-workers-{Guid.NewGuid():N}");
        var inputs = new SqlServerMetricInputStore(fixture.ConnectionString);
        var aggregation = new SqlServerMetricAggregationStore(fixture.ConnectionString);
        var first = await inputs.AppendAsync(CreateAppend(machine, "worker-a", 1), CancellationToken.None);
        var second = await inputs.AppendAsync(CreateAppend(machine, "worker-b", 2), CancellationToken.None);
        var target = new MetricAggregationCheckpoint(processor, first.StreamId, second.Position);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, null, target, [first, second]), CancellationToken.None);

        var workers = Enumerable.Range(0, 2)
            .Select(_ => new SqlServerProductionReferenceTimeConvergenceCoordinator(fixture.ConnectionString)
                .ConvergeAsync(target, machine, null, CancellationToken.None));
        var transitions = await Task.WhenAll(workers);
        Assert.All(transitions, transition => Assert.True(transition.IsCompleted));
        Assert.Equal(transitions[0], transitions[1]);
        Assert.Equal(0, transitions[0].ProductionStandardAuthorityRevision);
        var completedRevision = transitions[0].CompletedReferenceTimeRevision
            ?? throw new InvalidDataException("The completed transition has no reference-time revision.");
        Assert.Equal(2, completedRevision.Value);
        Assert.Equal(2, (await new SqlServerProductionReferenceTimeOutcomeStore(fixture.ConnectionString)
            .ReadAtRevisionAsync(processor, completedRevision, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ProjectionPrerequisiteConvergesExactNextRevisionAndRejectsUnownedReplay()
    {
        var machine = new MachineId(Guid.NewGuid());
        var processor = new MetricAggregationProcessorId($"reference-gate-{Guid.NewGuid():N}");
        var inputs = new SqlServerMetricInputStore(fixture.ConnectionString);
        var aggregation = new SqlServerMetricAggregationStore(fixture.ConnectionString);
        var first = await inputs.AppendAsync(CreateAppend(machine, "gate", 1), CancellationToken.None);
        var a1 = new MetricAggregationCheckpoint(processor, first.StreamId, first.Position);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, null, a1, [first]), CancellationToken.None);

        var prerequisite = new SqlServerOperationalMetricProjectionPrerequisite(fixture.ConnectionString);
        var replay = new OperationalMetricEvaluationBatchRequest(processor, first.StreamId, a1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await prerequisite.PrepareAsync(replay, CancellationToken.None);
        });

        var firstRequest = new OperationalMetricEvaluationBatchRequest(processor, first.StreamId, null);
        Assert.Equal(a1, await prerequisite.PrepareAsync(firstRequest, CancellationToken.None));
        Assert.Equal(a1, await prerequisite.PrepareAsync(replay, CancellationToken.None));

        var second = await inputs.AppendAsync(CreateAppend(machine, "gate-next", 2), CancellationToken.None);
        var a2 = new MetricAggregationCheckpoint(processor, first.StreamId, second.Position);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, a1, a2, [second]), CancellationToken.None);
        Assert.Equal(a2, await prerequisite.PrepareAsync(replay, CancellationToken.None));
        Assert.True((await new SqlServerProductionReferenceTimePublicationTransitionStore(fixture.ConnectionString)
            .ReadAsync(processor, a2.Position, CancellationToken.None))!.IsCompleted);
    }

    [Fact]
    public async Task PartialClaimResumesWithFrozenStartingRevisionAndStandardCut()
    {
        var machine = new MachineId(Guid.NewGuid());
        var processor = new MetricAggregationProcessorId($"reference-resume-{Guid.NewGuid():N}");
        var inputs = new SqlServerMetricInputStore(fixture.ConnectionString);
        var aggregation = new SqlServerMetricAggregationStore(fixture.ConnectionString);
        var first = await inputs.AppendAsync(CreateAppend(machine, "first", 1), CancellationToken.None);
        var second = await inputs.AppendAsync(CreateAppend(machine, "second", 2), CancellationToken.None);
        var third = await inputs.AppendAsync(CreateAppend(machine, "third", 3), CancellationToken.None);
        var a1 = new MetricAggregationCheckpoint(processor, first.StreamId, first.Position);
        var a2 = new MetricAggregationCheckpoint(processor, first.StreamId, third.Position);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, null, a1, [first]), CancellationToken.None);
        await aggregation.CommitAsync(new MetricAggregationCommit(processor, a1, a2, [second, third]), CancellationToken.None);

        var coordinator = new SqlServerProductionReferenceTimeConvergenceCoordinator(fixture.ConnectionString);
        var completedFirst = await coordinator.ConvergeAsync(a1, machine, null, CancellationToken.None);
        Assert.True(completedFirst.IsCompleted);
        var starting = completedFirst.CompletedReferenceTimeRevision!.Value;

        var transitionStore = new SqlServerProductionReferenceTimePublicationTransitionStore(fixture.ConnectionString);
        var pending = await transitionStore.BeginAsync(
            processor, a2.Position, a1.Position, starting, 0, CancellationToken.None);
        var standardAuthority = new SqlServerProductionStandardAuthority(fixture.ConnectionString);
        var standardCut = await standardAuthority.ReadCutAsync(0, CancellationToken.None);
        var sources = await aggregation.ReadProductionQuantityTransitionSourcesAsync(
            a2, machine, a1.Position, CancellationToken.None);
        Assert.Equal(2, sources.Count);
        var source = sources[0];
        var resolution = ProductionStandardResolver.Resolve(
            source.Evidence, source.ShiftOccurrenceId, source.ProductionDayId, standardCut);
        await new SqlServerProductionReferenceTimeTransitionOutcomeStore(fixture.ConnectionString)
            .AdmitAsync(processor, a2.Position,
                new PublishedProductionReferenceTimeOutcome(
                    new ProductionReferenceTimeAuthorityRevision(starting.Value + 1), resolution),
                CancellationToken.None);

        await standardAuthority.PublishAsync(new ProductionStandardVersion
        {
            VersionId = $"later-standard-{Guid.NewGuid():N}",
            CompanyId = source.Evidence.CompanyId,
            SiteId = source.Evidence.SiteId,
            PartId = source.Evidence.PartId
                ?? throw new InvalidDataException("Produced source has no part selector."),
            OperationId = source.Evidence.OperationId
                ?? throw new InvalidDataException("Produced source has no operation selector."),
            SecondsPerUnit = 5m,
            EffectiveFromUtc = source.Evidence.OccurredAtUtc.AddMinutes(-1),
            SourceReference = "approved-after-claim",
            PublishedRevision = 1,
        }, CancellationToken.None);

        var completed = await new SqlServerProductionReferenceTimeConvergenceCoordinator(fixture.ConnectionString)
            .ConvergeAsync(a2, machine, a1.Position, CancellationToken.None);
        Assert.True(completed.IsCompleted);
        Assert.Equal(pending.StartingReferenceTimeRevision, completed.StartingReferenceTimeRevision);
        Assert.Equal(0, completed.ProductionStandardAuthorityRevision);
        var finalRevision = completed.CompletedReferenceTimeRevision
            ?? throw new InvalidDataException("The completed transition has no reference-time revision.");
        Assert.Equal(starting.Value + 2, finalRevision.Value);
        var outcomes = await new SqlServerProductionReferenceTimeOutcomeStore(fixture.ConnectionString)
            .ReadAtRevisionAsync(processor, finalRevision, CancellationToken.None);
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.Equal(0, outcome.Resolution.AuthorityRevision));
        Assert.Equal(completed, await coordinator.ConvergeAsync(a2, machine, a1.Position, CancellationToken.None));
    }

    private static DurableMetricInputAppend CreateAppend(MachineId machine, string name, int minute)
    {
        var site = new SiteId("SITE-1");
        var shift = new ShiftId("SHIFT-A");
        var schedule = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var start = new DateTimeOffset(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);
        var sourceId = new ProductionQuantityEvidenceId($"{name}-{Guid.NewGuid():N}");
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId($"fact-{Guid.NewGuid():N}"),
            Key = MetricInputFactKeys.PartCountIncrement,
            Value = 2,
            Unit = MetricInputFactUnits.Count,
            StartsAtUtc = start.AddMinutes(minute),
            EndsAtUtc = start.AddMinutes(minute + 1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = site,
            MachineId = machine,
            ShiftId = shift,
            ShiftScheduleAssignmentId = schedule,
            PartId = new PartId("PART-1"),
            OperationId = new OperationId("OP-1"),
            SourceQuantityEvidenceId = sourceId,
        };
        return new DurableMetricInputAppend(
            MetricInputStreamId.ForMachine(machine), fact,
            new ShiftOccurrenceId(site, schedule, shift, start, start.AddHours(8)),
            new ProductionDayId(site, new DateOnly(2026, 9, 26)));
    }
}
