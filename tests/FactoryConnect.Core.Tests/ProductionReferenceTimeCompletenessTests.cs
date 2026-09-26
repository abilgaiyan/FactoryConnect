using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionReferenceTimeCompletenessTests
{
    private static readonly MachineId Machine = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly SiteId Site = new("site-1");
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly ShiftOccurrenceId Shift = new(
        Site, new ShiftScheduleAssignmentId("assignment-1"), new ShiftId("shift-1"),
        Start, Start.AddHours(8));
    private static readonly ProductionDayId Day = new(Site, new DateOnly(2026, 9, 26));

    [Fact]
    public void A_partial_sum_cannot_be_exposed_as_complete()
    {
        var sources = new[] { new ProductionQuantityEvidenceId("a"), new ProductionQuantityEvidenceId("b") };
        var resolved = Outcome("a", ProductionReferenceTimeResolutionStatus.Resolved);

        Assert.False(ProductionReferenceTimeCompleteness.IsComplete(sources, [resolved], Machine, Shift, null));
        Assert.False(ProductionReferenceTimeCompleteness.IsComplete(
            sources, [resolved, Outcome("b", ProductionReferenceTimeResolutionStatus.MissingStandard)], Machine, Shift, null));
        Assert.True(ProductionReferenceTimeCompleteness.IsComplete(
            sources, [resolved, Outcome("b", ProductionReferenceTimeResolutionStatus.Resolved)], Machine, Shift, null));
    }

    [Fact]
    public void A_day_cannot_hide_an_unresolved_source_in_another_shift()
    {
        var sources = new[] { new ProductionQuantityEvidenceId("a"), new ProductionQuantityEvidenceId("b") };
        var laterShift = new ShiftOccurrenceId(
            Site, new ShiftScheduleAssignmentId("assignment-2"), new ShiftId("shift-2"),
            Start.AddHours(8), Start.AddHours(16));

        Assert.False(ProductionReferenceTimeCompleteness.IsComplete(
            sources,
            [Outcome("a", ProductionReferenceTimeResolutionStatus.Resolved),
                Outcome("b", ProductionReferenceTimeResolutionStatus.AmbiguousStandard, laterShift)],
            Machine, null, Day));
    }

    private static ProductionReferenceTimeResolution Outcome(
        string id,
        ProductionReferenceTimeResolutionStatus status,
        ShiftOccurrenceId? shift = null) => new()
    {
        SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(id),
        CompanyId = new CompanyId("company-1"),
        SiteId = Site,
        MachineId = Machine,
        ShiftOccurrenceId = shift ?? Shift,
        ProductionDayId = Day,
        OccurredAtUtc = (shift ?? Shift).StartsAtUtc.AddMinutes(1),
        ProducedUnits = 1,
        AuthorityRevision = 1,
        Status = status,
        SelectedStandardVersionId = status == ProductionReferenceTimeResolutionStatus.Resolved ? "version-1" : null,
        SelectedStandardSourceReference = status == ProductionReferenceTimeResolutionStatus.Resolved ? "approved-plan" : null,
        IdealDurationSeconds = status == ProductionReferenceTimeResolutionStatus.Resolved ? 10 : null,
        ConflictingStandardVersionIds = status == ProductionReferenceTimeResolutionStatus.AmbiguousStandard
            ? ["version-1", "version-2"] : [],
    };
}
