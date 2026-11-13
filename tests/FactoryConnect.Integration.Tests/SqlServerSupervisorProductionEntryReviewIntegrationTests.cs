using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorProductionEntryReviewIntegrationTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public SqlServerSupervisorProductionEntryReviewIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReviewIsImmutableMachineBoundAndCannotBeSelfReviewed()
    {
        var machine = MachineId.New();
        var day = new ProductionDayId(new SiteId("SUP-REVIEW-SITE"), new DateOnly(2026, 11, 13));
        var line = new ProductionLineId("SUP-REVIEW-LINE");
        var start = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(8);
        var occurrence = new ShiftOccurrenceId(day.SiteId,
            new ShiftScheduleAssignmentId("REVIEW-ASSIGN"), new ShiftId("SHIFT-R"), start, end);
        var roster = new MachineShiftOccurrenceRoster(machine, line, day,
            new MachineShiftOccurrenceRosterRevision(1),
            [new MachineShiftOccurrenceOwnership(machine, line, occurrence, day)]);
        await new SqlServerMachineShiftOccurrenceRosterStore(_fixture.ConnectionString)
            .CommitAsync(new MachineShiftOccurrenceRosterCommit(null, roster), CancellationToken.None);

        var entry = new SupervisorProductionEntryRecord(Guid.NewGuid(), machine.Value,
            "REVIEW-ASSIGN", "SHIFT-R", start, end, "ORDER-R", "PART-R", "OP-R",
            null, "declaration-author", start.AddMinutes(10), 8, 1);
        Assert.True(await new SqlServerSupervisorProductionEntryWriter(_fixture.ConnectionString).AppendAsync(entry));
        var store = new SqlServerSupervisorProductionEntryReviewStore(_fixture.ConnectionString);
        var decisionId = Guid.NewGuid();
        var at = start.AddHours(1);

        var wrongMachine = await Assert.ThrowsAsync<SqlException>(() =>
            store.DecideAsync(Guid.NewGuid(), entry.EntryId, decisionId, "Approved", "Checked",
                "reviewer", at));
        Assert.Equal(51026, wrongMachine.Number);

        var selfReview = await Assert.ThrowsAsync<SqlException>(() =>
            store.DecideAsync(machine.Value, entry.EntryId, decisionId, "Approved", "Checked",
                "declaration-author", at));
        Assert.Equal(51027, selfReview.Number);

        Assert.True(await store.DecideAsync(machine.Value, entry.EntryId, decisionId,
            "Approved", "Checked", "reviewer", at));
        Assert.False(await store.DecideAsync(machine.Value, entry.EntryId, decisionId,
            "Approved", "Checked", "reviewer", at.AddMinutes(1)));

        var conflict = await Assert.ThrowsAsync<SqlException>(() =>
            store.DecideAsync(machine.Value, entry.EntryId, Guid.NewGuid(),
                "Rejected", "Changed mind", "reviewer", at));
        Assert.Equal(51028, conflict.Number);

        var reviews = await store.ListAsync(machine.Value, start, end);
        var review = Assert.Single(reviews);
        Assert.Equal(entry.EntryId, review.EntryId);
        Assert.Equal(decisionId, review.DecisionId);
        Assert.Equal("Approved", review.Decision);
        Assert.Equal("Checked", review.Reason);
        Assert.Equal("reviewer", review.ReviewedBy);
        Assert.Equal(at, review.ReviewedAtUtc);
    }
}
