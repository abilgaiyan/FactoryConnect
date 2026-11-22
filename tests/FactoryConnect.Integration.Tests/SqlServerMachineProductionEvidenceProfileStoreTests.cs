using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMachineProductionEvidenceProfileStoreTests
{
    [Fact]
    public async Task AppendIsIdempotentAndRejectsStaleOrConflictingRevisions()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
            connection, TimeSpan.FromMinutes(2), CancellationToken.None);
        var store = new SqlServerMachineProductionEvidenceProfileStore(database.ConnectionString);
        var machine = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var first = new MachineProductionEvidenceProfile(Guid.NewGuid(), machine, 1,
            "StateOnly", "execution", null, null, start, "commissioner", start);
        Assert.True(await store.AppendAsync(first, null));
        Assert.False(await store.AppendAsync(first, null));
        await Assert.ThrowsAsync<SqlException>(() =>
            store.AppendAsync(first with { StateDataItemId = "different" }, first.ProfileRevisionId));
        var second = first with
        {
            ProfileRevisionId = Guid.NewGuid(), RevisionNumber = 2,
            EffectiveFromUtc = start.AddDays(1), ConfiguredAtUtc = start.AddDays(1),
            EvidenceMode = "CncCounterAndState", CounterDataItemId = "part_count",
            CounterSemantics = "CumulativeResettable"
        };
        await Assert.ThrowsAsync<SqlException>(() => store.AppendAsync(second, null));
        await Assert.ThrowsAsync<SqlException>(() => store.AppendAsync(
            second with { EffectiveFromUtc = start }, first.ProfileRevisionId));
        Assert.True(await store.AppendAsync(second, first.ProfileRevisionId));
        Assert.Equal(first, await store.GetEffectiveAsync(machine, start.AddHours(1)));
        Assert.Equal(second, await store.GetEffectiveAsync(machine, start.AddDays(2)));
        Assert.Null(await store.GetEffectiveAsync(machine, start.AddSeconds(-1)));
        Assert.Equal(new[] { first, second }, await store.ListAsync(machine));
    }

    [Fact]
    public async Task StateOnlyRejectsCounterAndUnknownMachineHasNoProfile()
    {
        var store = new SqlServerMachineProductionEvidenceProfileStore(
            "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true");
        var utc = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var profile = new MachineProductionEvidenceProfile(Guid.NewGuid(), Guid.NewGuid(),
            1, "StateOnly", "execution", "part_count", "CyclePulse", utc, "test", utc);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(profile, null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetEffectiveAsync(Guid.Empty, utc));
    }
}
