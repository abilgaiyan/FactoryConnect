using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorProductionEntryIntegrationTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public SqlServerSupervisorProductionEntryIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExactRosterEntryRoundTripsAndRetryIsIdempotent()
    {
        var machine = MachineId.New();
        var day = new ProductionDayId(new SiteId("SUPERVISOR-SITE"), new DateOnly(2026, 11, 12));
        var line = new ProductionLineId("SUPERVISOR-LINE");
        var start = new DateTimeOffset(2026, 11, 12, 6, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(8);
        var occurrence = new ShiftOccurrenceId(day.SiteId,
            new ShiftScheduleAssignmentId("SUPERVISOR-ASSIGN"), new ShiftId("SHIFT-A"), start, end);
        var roster = new MachineShiftOccurrenceRoster(machine, line, day,
            new MachineShiftOccurrenceRosterRevision(1),
            [new MachineShiftOccurrenceOwnership(machine, line, occurrence, day)]);
        var rosterStore = new SqlServerMachineShiftOccurrenceRosterStore(_fixture.ConnectionString);
        await rosterStore.CommitAsync(new MachineShiftOccurrenceRosterCommit(null, roster), CancellationToken.None);

        var entry = new SupervisorProductionEntryRecord(Guid.NewGuid(), machine.Value,
            "SUPERVISOR-ASSIGN", "SHIFT-A", start, end,
            "ORDER-1", "PART-1", "OP-1", "Production started", "test-supervisor", start.AddHours(1));
        var writer = new SqlServerSupervisorProductionEntryWriter(_fixture.ConnectionString);
        var reader = new SqlServerSupervisorProductionEntryReader(_fixture.ConnectionString);
        Assert.True(await writer.AppendAsync(entry));
        Assert.False(await writer.AppendAsync(entry));
        // HTTP retries regenerate the server-side timestamp. The same declaration
        // must remain idempotent without changing its original recorded time.
        Assert.False(await writer.AppendAsync(entry with { RecordedAtUtc = entry.RecordedAtUtc.AddMinutes(2) }));
        Assert.Contains(entry, await reader.ListAsync(machine.Value, start, end));

        var conflict = entry with { ProductionOrder = "ORDER-2" };
        var error = await Assert.ThrowsAsync<SqlException>(() => writer.AppendAsync(conflict));
        Assert.Equal(51025, error.Number);
        Assert.Single(await reader.ListAsync(machine.Value, start, end));
    }

    [Fact]
    public async Task MissingRosterRejectsEntryWithoutPersistence()
    {
        var machine = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 12, 6, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(8);
        var entry = new SupervisorProductionEntryRecord(Guid.NewGuid(), machine,
            "UNKNOWN-ASSIGN", "SHIFT-A", start, end,
            "ORDER-1", "PART-1", "OP-1", null, "test-supervisor", start.AddHours(1));
        var writer = new SqlServerSupervisorProductionEntryWriter(_fixture.ConnectionString);
        var error = await Assert.ThrowsAsync<SqlException>(() => writer.AppendAsync(entry));
        Assert.Equal(51024, error.Number);
        var reader = new SqlServerSupervisorProductionEntryReader(_fixture.ConnectionString);
        Assert.Empty(await reader.ListAsync(machine, start, end));
    }
}
