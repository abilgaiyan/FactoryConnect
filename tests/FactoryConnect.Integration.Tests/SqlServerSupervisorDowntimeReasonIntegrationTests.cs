using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSupervisorDowntimeReasonIntegrationTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public SqlServerSupervisorDowntimeReasonIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReasonMasterCreatesUpdatesAndDeactivatesWithoutLosingHistory()
    {
        var store = new SqlServerSupervisorDowntimeReasonStore(_fixture.ConnectionString);
        var code = "SUP5-" + Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertAsync(new SupervisorDowntimeReasonRecord(code, "Machine stopped", "Initial reason", true), now);
        Assert.Contains(await store.ListAsync(), reason => reason.ReasonCode == code && reason.IsActive);

        await store.UpsertAsync(new SupervisorDowntimeReasonRecord(code, "Machine stopped - revised", null, false), now.AddMinutes(1));
        Assert.DoesNotContain(await store.ListAsync(), reason => reason.ReasonCode == code);
        Assert.Contains(await store.ListAsync(activeOnly: false), reason =>
            reason.ReasonCode == code && reason.DisplayName == "Machine stopped - revised" && !reason.IsActive);
    }

    [Fact]
    public async Task ReasonMasterRejectsInvalidCodesBeforeWriting()
    {
        var store = new SqlServerSupervisorDowntimeReasonStore(_fixture.ConnectionString);
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(new SupervisorDowntimeReasonRecord(" ", "Invalid", null, true), now));
    }
}
