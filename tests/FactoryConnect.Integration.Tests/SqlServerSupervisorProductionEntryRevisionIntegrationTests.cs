using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorProductionEntryRevisionIntegrationTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public SqlServerSupervisorProductionEntryRevisionIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CorrectionsAreAppendOnlyMachineBoundAndRejectStaleHeads()
    {
        var machine = MachineId.New();
        var day = new ProductionDayId(new SiteId("SUP-REVISION-SITE"), new DateOnly(2026, 11, 14));
        var line = new ProductionLineId("SUP-REVISION-LINE");
        var start = new DateTimeOffset(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(8);
        var occurrence = new ShiftOccurrenceId(day.SiteId,
            new ShiftScheduleAssignmentId("REVISION-ASSIGN"), new ShiftId("SHIFT-R"), start, end);
        var roster = new MachineShiftOccurrenceRoster(machine, line, day,
            new MachineShiftOccurrenceRosterRevision(1),
            [new MachineShiftOccurrenceOwnership(machine, line, occurrence, day)]);
        await new SqlServerMachineShiftOccurrenceRosterStore(_fixture.ConnectionString)
            .CommitAsync(new MachineShiftOccurrenceRosterCommit(null, roster), CancellationToken.None);

        var entry = new SupervisorProductionEntryRecord(Guid.NewGuid(), machine.Value,
            "REVISION-ASSIGN", "SHIFT-R", start, end, "ORDER-1", "PART-1", "OP-1",
            null, "original-author", start.AddMinutes(10), 8, 1);
        Assert.True(await new SqlServerSupervisorProductionEntryWriter(_fixture.ConnectionString).AppendAsync(entry));
        var store = new SqlServerSupervisorProductionEntryRevisionStore(_fixture.ConnectionString);
        var first = new SupervisorProductionEntryRevisionRecord(Guid.NewGuid(), entry.EntryId,
            null, 1, "ORDER-2", "PART-1", "OP-1", "corrected", 9, 1,
            "Operator correction", "supervisor", start.AddMinutes(30));

        var wrongMachine = await Assert.ThrowsAsync<SqlException>(() =>
            store.AppendAsync(Guid.NewGuid(), null, first));
        Assert.Equal(51030, wrongMachine.Number);
        Assert.True(await store.AppendAsync(machine.Value, null, first));
        Assert.False(await store.AppendAsync(machine.Value, null, first));

        var stale = first with { RevisionId = Guid.NewGuid(), RevisionNumber = 2 };
        var conflict = await Assert.ThrowsAsync<SqlException>(() =>
            store.AppendAsync(machine.Value, null, stale));
        Assert.Equal(51032, conflict.Number);

        var second = stale with { PreviousRevisionId = first.RevisionId };
        Assert.True(await store.AppendAsync(machine.Value, first.RevisionId, second));
        var history = await store.ListAsync(machine.Value, entry.EntryId);
        Assert.Equal(2, history.Count);
        Assert.Equal(first.RevisionId, history[0].RevisionId);
        Assert.Equal(second.RevisionId, history[1].RevisionId);
        Assert.Empty(await store.ListAsync(Guid.NewGuid(), entry.EntryId));

        var reviewStore = new SqlServerSupervisorProductionEntryRevisionReviewStore(_fixture.ConnectionString);
        var reviewId = Guid.NewGuid();
        var reviewAt = start.AddHours(2);
        var selfReview = await Assert.ThrowsAsync<SqlException>(() =>
            reviewStore.DecideAsync(machine.Value, first.RevisionId, reviewId, "Approved",
                "Checked correction", "supervisor", reviewAt));
        Assert.Equal(51037, selfReview.Number);
        var wrongReviewMachine = await Assert.ThrowsAsync<SqlException>(() =>
            reviewStore.DecideAsync(Guid.NewGuid(), first.RevisionId, reviewId, "Approved",
                "Checked correction", "reviewer", reviewAt));
        Assert.Equal(51036, wrongReviewMachine.Number);
        Assert.True(await reviewStore.DecideAsync(machine.Value, first.RevisionId, reviewId,
            "Approved", "Checked correction", "reviewer", reviewAt));
        Assert.False(await reviewStore.DecideAsync(machine.Value, first.RevisionId, reviewId,
            "Approved", "Checked correction", "reviewer", reviewAt.AddMinutes(1)));
        var conflictingReview = await Assert.ThrowsAsync<SqlException>(() =>
            reviewStore.DecideAsync(machine.Value, first.RevisionId, Guid.NewGuid(),
                "Rejected", "Changed decision", "reviewer", reviewAt));
        Assert.Equal(51038, conflictingReview.Number);
        var revisionReviews = await reviewStore.ListAsync(machine.Value, start, end);
        Assert.Equal(2, revisionReviews.Count);
        Assert.Equal("Approved", Assert.Single(revisionReviews, item => item.RevisionId == first.RevisionId).Decision);
        Assert.Equal("Pending", Assert.Single(revisionReviews, item => item.RevisionId == second.RevisionId).Decision);
        Assert.Empty(await reviewStore.ListAsync(Guid.NewGuid(), start, end));

        var original = Assert.Single(await new SqlServerSupervisorProductionEntryReader(_fixture.ConnectionString)
            .ListAsync(machine.Value, start, end));
        Assert.Equal("ORDER-1", original.ProductionOrder);
        Assert.Equal(8, original.GoodQuantity);
    }
}
