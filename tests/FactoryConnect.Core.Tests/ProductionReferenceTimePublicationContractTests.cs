using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionReferenceTimePublicationContractTests
{
    [Fact]
    public void PublicationCutRetainsExactIndependentRevisions()
    {
        var machine = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var checkpoint = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregation-one"),
            MetricInputStreamId.ForMachine(machine),
            new MetricInputPosition(7));

        var cut = new ProductionReferenceTimePublicationCut(
            checkpoint, new ProductionReferenceTimeAuthorityRevision(2));

        Assert.Equal(checkpoint, cut.AggregationRevision);
        Assert.Equal(checkpoint.ProcessorId, cut.MetricAggregationProcessorId);
        Assert.Equal(checkpoint.Position, cut.MetricAggregationPosition);
        Assert.Equal(checkpoint.StreamId, cut.MetricInputStreamId);
        Assert.Equal(2, cut.ReferenceTimeRevision.Value);
        Assert.NotEqual(cut, new ProductionReferenceTimePublicationCut(
            checkpoint, new ProductionReferenceTimeAuthorityRevision(3)));
        Assert.Throws<ArgumentNullException>(() => new ProductionReferenceTimePublicationCut(
            null!, ProductionReferenceTimeAuthorityRevision.Empty));
    }

    [Fact]
    public void PublishedOutcomePinsSourceAndCopiesConflictLineage()
    {
        var conflicts = new[] { "standard-a", "standard-b" };
        var start = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        var site = new SiteId("site-one");
        var resolution = new ProductionReferenceTimeResolution
        {
            SourceQuantityEvidenceId = new ProductionQuantityEvidenceId("quantity-one"),
            CompanyId = new CompanyId("company-one"),
            SiteId = site,
            MachineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            ShiftOccurrenceId = new ShiftOccurrenceId(
                site, new ShiftScheduleAssignmentId("schedule-one"), new ShiftId("shift-one"),
                start, start.AddHours(8)),
            ProductionDayId = new ProductionDayId(site, new DateOnly(2026, 9, 26)),
            OccurredAtUtc = start.AddMinutes(1),
            ProducedUnits = 2,
            AuthorityRevision = 3,
            Status = ProductionReferenceTimeResolutionStatus.AmbiguousStandard,
            ConflictingStandardVersionIds = conflicts,
        };

        var published = new PublishedProductionReferenceTimeOutcome(
            new ProductionReferenceTimeAuthorityRevision(4), resolution);
        conflicts[0] = "changed";

        Assert.Equal("standard-a", published.Resolution.ConflictingStandardVersionIds[0]);
        Assert.Equal(4, published.PublicationRevision.Value);
        Assert.Equal(3, published.StandardAuthorityRevision);
        Assert.Equal(resolution.SourceQuantityEvidenceId, published.SourceQuantityEvidenceId);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublishedProductionReferenceTimeOutcome(
            ProductionReferenceTimeAuthorityRevision.Empty, resolution));
    }
}
