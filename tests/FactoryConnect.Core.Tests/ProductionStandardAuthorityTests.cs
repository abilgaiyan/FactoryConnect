using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionStandardAuthorityTests
{
    private static readonly MachineId Machine = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly SiteId Site = new("site-1");
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly ShiftOccurrenceId Shift = new(
        Site, new ShiftScheduleAssignmentId("assignment-1"), new ShiftId("shift-1"), Start, Start.AddHours(8));
    private static readonly ProductionDayId Day = new(Site, new DateOnly(2026, 9, 26));
    private static readonly string[] AmbiguousMachineStandardVersionIds = ["machine-a", "machine-b"];

    [Fact]
    public void MachineSpecificStandardTakesPrecedenceWithoutHidingAnOverlap()
    {
        var authority = new InMemoryProductionStandardAuthority();
        authority.Publish(Standard("site", 1, 10));
        var siteCut = authority.ReadCurrentCut();
        authority.Publish(Standard("machine-a", 2, 8, Machine));
        var machineCut = authority.ReadCurrentCut();
        authority.Publish(Standard("machine-b", 3, 9, Machine));
        var evidence = Evidence("a", 2);

        Assert.Equal(20m, ProductionStandardResolver.Resolve(evidence, Shift, Day, siteCut).IdealDurationSeconds);
        Assert.Equal(16m, ProductionStandardResolver.Resolve(evidence, Shift, Day, machineCut).IdealDurationSeconds);
        var ambiguous = ProductionStandardResolver.Resolve(evidence, Shift, Day, authority.ReadCurrentCut());
        Assert.Equal(ProductionReferenceTimeResolutionStatus.AmbiguousStandard, ambiguous.Status);
        Assert.Equal(AmbiguousMachineStandardVersionIds, ambiguous.ConflictingStandardVersionIds);
        Assert.Null(ambiguous.IdealDurationSeconds);
    }

    [Fact]
    public void OccurrenceTimeAndPublicationCutBothConstrainSelection()
    {
        var authority = new InMemoryProductionStandardAuthority();
        var emptyCut = authority.ReadCurrentCut();
        authority.Publish(Standard("future", 1, 10) with { EffectiveFromUtc = Start.AddHours(2) });
        var evidence = Evidence("a", 2);

        Assert.Equal(ProductionReferenceTimeResolutionStatus.MissingStandard,
            ProductionStandardResolver.Resolve(evidence, Shift, Day, emptyCut).Status);
        Assert.Equal(ProductionReferenceTimeResolutionStatus.MissingStandard,
            ProductionStandardResolver.Resolve(evidence, Shift, Day, authority.ReadCurrentCut()).Status);
        Assert.Equal(20m, ProductionStandardResolver.Resolve(
            evidence with { OccurredAtUtc = Start.AddHours(2) }, Shift, Day, authority.ReadCurrentCut()).IdealDurationSeconds);
    }

    [Fact]
    public async Task HistoricalAggregationRevisionCannotHideASecondProducedSource()
    {
        var standards = new InMemoryProductionStandardAuthority();
        standards.Publish(Standard("site", 1, 10));
        var cut = standards.ReadCurrentCut();
        var outcomes = new InMemoryProductionReferenceTimeAuthority();
        var aggregates = new InMemoryMetricAggregationStore();
        var processor = new MetricAggregationProcessorId("aggregate-machine-1");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var first = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(1));
        var second = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(2));
        var evidenceA = Evidence("a", 1);
        var evidenceB = Evidence("b", 1);

        await aggregates.CommitAsync(new MetricAggregationCommit(
            processor, null, first, [Input(stream, 1, evidenceA)]), CancellationToken.None);
        outcomes.ResolveAndRecord(evidenceA, Shift, Day, cut);
        await aggregates.CommitAsync(new MetricAggregationCommit(
            processor, first, second, [Input(stream, 2, evidenceB)]), CancellationToken.None);

        var period = new OperationalMetricPeriodId.Shift(Shift);
        Assert.True(outcomes.IsCompleteAtRevision(aggregates, first, period));
        Assert.False(outcomes.IsCompleteAtRevision(aggregates, second, period));
        outcomes.ResolveAndRecord(evidenceB, Shift, Day, cut);
        Assert.True(outcomes.IsCompleteAtRevision(aggregates, second, period));
    }

    [Fact]
    public void OrdinaryReplayCannotAdoptNewStandardAuthority()
    {
        var standards = new InMemoryProductionStandardAuthority();
        var originalCut = standards.ReadCurrentCut();
        var outcomes = new InMemoryProductionReferenceTimeAuthority();
        var evidence = Evidence("a", 2);
        var original = outcomes.ResolveAndRecord(evidence, Shift, Day, originalCut);

        standards.Publish(Standard("site", 1, 10));

        Assert.Equal(ProductionReferenceTimeResolutionStatus.MissingStandard, original.Status);
        Assert.Same(original, outcomes.ResolveAndRecord(evidence, Shift, Day, originalCut));
        Assert.Throws<InvalidOperationException>(() => outcomes.ResolveAndRecord(
            evidence, Shift, Day, standards.ReadCurrentCut()));
    }

    [Fact]
    public void MissingPartCannotFallBackToAnotherStandard()
    {
        var standards = new InMemoryProductionStandardAuthority();
        standards.Publish(Standard("site", 1, 10));
        var result = ProductionStandardResolver.Resolve(
            Evidence("a", 1) with { PartId = null }, Shift, Day, standards.ReadCurrentCut());

        Assert.Equal(ProductionReferenceTimeResolutionStatus.MissingIdentity, result.Status);
        Assert.Null(result.IdealDurationSeconds);
    }

    private static ProductionStandardVersion Standard(string id, long revision, decimal seconds, MachineId? machine = null) => new()
    {
        VersionId = id,
        CompanyId = new CompanyId("company-1"),
        SiteId = Site,
        PartId = new PartId("part-1"),
        OperationId = new OperationId("operation-1"),
        MachineId = machine,
        SecondsPerUnit = seconds,
        EffectiveFromUtc = Start,
        SourceReference = "engineering-approved",
        PublishedRevision = revision,
    };

    private static ProductionQuantityEvidence Evidence(string id, int units) => new()
    {
        Id = new ProductionQuantityEvidenceId(id),
        CompanyId = new CompanyId("company-1"),
        SiteId = Site,
        MachineId = Machine,
        ShiftId = Shift.ShiftId,
        PartId = new PartId("part-1"),
        OperationId = new OperationId("operation-1"),
        OccurredAtUtc = Start.AddMinutes(1),
        PartCountIncrement = units,
    };

    private static PositionedMetricInputFact Input(
        MetricInputStreamId stream, ulong position, ProductionQuantityEvidence evidence) => new(
        stream,
        new MetricInputPosition(position),
        new DurableMetricInputFact
        {
            Id = new MetricInputFactId($"fact-{position}"),
            Key = MetricInputFactKeys.PartCountIncrement,
            Value = evidence.PartCountIncrement!.Value,
            Unit = MetricInputFactUnits.Count,
            StartsAtUtc = evidence.OccurredAtUtc,
            EndsAtUtc = evidence.OccurredAtUtc,
            CompanyId = evidence.CompanyId,
            SiteId = evidence.SiteId,
            MachineId = evidence.MachineId,
            ShiftId = evidence.ShiftId,
            ShiftScheduleAssignmentId = Shift.ShiftScheduleAssignmentId,
            PartId = evidence.PartId,
            OperationId = evidence.OperationId,
            SourceQuantityEvidenceId = evidence.Id,
        },
        Shift,
        Day);
}
