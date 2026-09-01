using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Core.Tests;

public sealed class RosterValidatedProductionContextProcessingStoreTests
{
    [Fact]
    public async Task MissingRosterCoverageRejectsPublicationBeforeInnerCommit()
    {
        var fixture = CreateFixture();

        var exception = await Assert.ThrowsAsync<MachineShiftRosterCoverageRequiredException>(
            () => fixture.Guard.CommitAsync(fixture.Commit(), CancellationToken.None));

        Assert.Equal(fixture.MachineId, exception.MachineId);
        Assert.Equal(fixture.Day, exception.ProductionDayId);
        Assert.Equal(0, fixture.Inner.CommitCount);
    }

    [Fact]
    public async Task CoveredEmptyRosterRejectsOccurrenceAsAuthoritativeMismatch()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([]);

        await Assert.ThrowsAsync<MachineShiftOccurrenceOwnershipMismatchException>(
            () => fixture.Guard.CommitAsync(fixture.Commit(), CancellationToken.None));

        Assert.Equal(0, fixture.Inner.CommitCount);
    }

    [Fact]
    public async Task DifferentOccurrenceRejectsPublicationWithoutAdvancingInnerStore()
    {
        var fixture = CreateFixture();
        var other = new ShiftOccurrenceId(
            fixture.SiteId,
            fixture.Occurrence.ShiftScheduleAssignmentId,
            new ShiftId("SHIFT-B"),
            fixture.Occurrence.StartsAtUtc,
            fixture.Occurrence.EndsAtUtc);
        await fixture.PublishRosterAsync([other]);

        await Assert.ThrowsAsync<MachineShiftOccurrenceOwnershipMismatchException>(
            () => fixture.Guard.CommitAsync(fixture.Commit(), CancellationToken.None));

        Assert.Equal(0, fixture.Inner.CommitCount);
    }

    [Fact]
    public async Task ExactMachineLineDayAndOccurrenceAllowsPublication()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([fixture.Occurrence]);
        var commit = fixture.Commit();

        await fixture.Guard.CommitAsync(commit, CancellationToken.None);

        Assert.Equal(1, fixture.Inner.CommitCount);
        Assert.Same(commit, fixture.Inner.LastCommit);
    }

    [Fact]
    public async Task MissingCoverageCanBeMaterializedAndSamePublicationRetried()
    {
        var fixture = CreateFixture();
        var commit = fixture.Commit();
        await Assert.ThrowsAsync<MachineShiftRosterCoverageRequiredException>(
            () => fixture.Guard.CommitAsync(commit, CancellationToken.None));
        Assert.Equal(0, fixture.Inner.CommitCount);

        await fixture.PublishRosterAsync([fixture.Occurrence]);
        await fixture.Guard.CommitAsync(commit, CancellationToken.None);

        Assert.Equal(1, fixture.Inner.CommitCount);
    }

    [Fact]
    public async Task FactWithConflictingLineIsRejectedEvenWhenOccurrenceExists()
    {
        var fixture = CreateFixture();
        await fixture.PublishRosterAsync([fixture.Occurrence]);
        var commit = fixture.Commit(new ProductionLineId("LINE-2"));

        await Assert.ThrowsAsync<MachineShiftOccurrenceOwnershipMismatchException>(
            () => fixture.Guard.CommitAsync(commit, CancellationToken.None));

        Assert.Equal(0, fixture.Inner.CommitCount);
    }

    private static Fixture CreateFixture()
    {
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var siteId = new SiteId("SITE-A");
        var lineId = new ProductionLineId("LINE-1");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 9, 1));
        var occurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("ASSIGN-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 9, 1, 0, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.Zero));
        var rosterStore = new InMemoryMachineShiftOccurrenceRosterStore();
        var inner = new RecordingStore();
        var guard = new RosterValidatedProductionContextProcessingStore(
            inner,
            rosterStore,
            [new MachineShiftScheduleScope(machineId, siteId, lineId)]);
        return new Fixture(machineId, siteId, lineId, day, occurrence, rosterStore, inner, guard);
    }

    private sealed record Fixture(
        MachineId MachineId,
        SiteId SiteId,
        ProductionLineId LineId,
        ProductionDayId Day,
        ShiftOccurrenceId Occurrence,
        InMemoryMachineShiftOccurrenceRosterStore RosterStore,
        RecordingStore Inner,
        RosterValidatedProductionContextProcessingStore Guard)
    {
        public async Task PublishRosterAsync(IReadOnlyList<ShiftOccurrenceId> occurrences)
        {
            var roster = new MachineShiftOccurrenceRoster(
                MachineId,
                LineId,
                Day,
                new MachineShiftOccurrenceRosterRevision(1),
                occurrences.Select(occurrence => new MachineShiftOccurrenceOwnership(
                    MachineId,
                    LineId,
                    occurrence,
                    Day)));
            await RosterStore.CommitAsync(
                new MachineShiftOccurrenceRosterCommit(null, roster),
                CancellationToken.None);
        }

        public ProductionContextProcessingCommit Commit(ProductionLineId? factLine = null)
        {
            var fact = new DurableMetricInputFact
            {
                Id = new MetricInputFactId("FACT-1"),
                Key = "ActualProductionTime",
                Value = 60m,
                Unit = "Seconds",
                StartsAtUtc = Occurrence.StartsAtUtc,
                EndsAtUtc = Occurrence.EndsAtUtc,
                CompanyId = new CompanyId("COMPANY-A"),
                SiteId = SiteId,
                ProductionLineId = factLine ?? LineId,
                MachineId = MachineId,
                ShiftId = Occurrence.ShiftId,
                ShiftScheduleAssignmentId = Occurrence.ShiftScheduleAssignmentId,
            };
            var streamId = new ObservationStreamId(MachineId, "activity");
            return new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = null,
                NextCheckpoint = new ObservationProcessingCheckpoint(
                    new ObservationProcessorId("processor"),
                    streamId,
                    new ObservationPosition(1)),
                MetricFacts = [fact],
                MetricInputs =
                [
                    new DurableMetricInputAppend(
                        MetricInputStreamId.ForMachine(MachineId),
                        fact,
                        Occurrence,
                        Day),
                ],
            };
        }
    }

    private sealed class RecordingStore : IProductionContextProcessingStore
    {
        public int CommitCount { get; private set; }

        public ProductionContextProcessingCommit? LastCommit { get; private set; }

        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ObservationProcessingCheckpoint?>(null);

        public Task CommitAsync(
            ProductionContextProcessingCommit commit,
            CancellationToken cancellationToken)
        {
            CommitCount++;
            LastCommit = commit;
            return Task.CompletedTask;
        }
    }
}
