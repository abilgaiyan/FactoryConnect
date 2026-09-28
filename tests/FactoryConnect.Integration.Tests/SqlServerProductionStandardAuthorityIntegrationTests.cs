using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerProductionStandardAuthorityIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerProductionStandardAuthorityIntegrationTests(SqlServerTestDatabaseFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task DurableAuthorityPreservesS0HistoricalCutsReplayAndConflictSemantics()
    {
        var authority = new SqlServerProductionStandardAuthority(_fixture.ConnectionString);
        var empty = await authority.ReadCutAsync(0);
        Assert.Equal(0, empty.Revision);
        Assert.Empty(empty.Versions);

        var first = CreateVersion("standard-a", 1, 5m, null);
        Assert.Equal(1, await authority.PublishAsync(first));
        Assert.Equal(1, await authority.PublishAsync(first));

        var second = CreateVersion(
            "standard-b",
            2,
            4m,
            new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111")));
        Assert.Equal(2, await authority.PublishAsync(second));

        var cut1 = await authority.ReadCutAsync(1);
        Assert.Equal(1, cut1.Revision);
        Assert.Equal("standard-a", Assert.Single(cut1.Versions).VersionId);

        var cut2 = await authority.ReadCutAsync(2);
        Assert.Equal(2, cut2.Revision);
        Assert.Equal(["standard-a", "standard-b"], cut2.Versions.Select(static version => version.VersionId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.PublishAsync(first with { SecondsPerUnit = 7m }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.PublishAsync(CreateVersion("standard-skipped", 4, 8m, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.ReadCutAsync(3));

        var current = await authority.ReadCurrentCutAsync();
        Assert.Equal(2, current.Revision);
        Assert.Equal(2, current.Versions.Count);
        Assert.Equal("standard-a", (await authority.ReadCutAsync(1)).Versions.Single().VersionId);
    }

    [Fact]
    public async Task PublicationRejectsSecondsPerUnitThatCannotRoundTripExactly()
    {
        var authority = new SqlServerProductionStandardAuthority(_fixture.ConnectionString);
        var current = await authority.ReadCurrentCutAsync();
        var proposed = CreateVersion(
            $"precision-{Guid.NewGuid():N}",
            current.Revision + 1,
            1.1234567m,
            null);

        await Assert.ThrowsAsync<ArgumentException>(() => authority.PublishAsync(proposed));

        var after = await authority.ReadCurrentCutAsync();
        Assert.Equal(current.Revision, after.Revision);
        Assert.DoesNotContain(after.Versions, version => version.VersionId == proposed.VersionId);
    }

    private static ProductionStandardVersion CreateVersion(
        string versionId,
        long revision,
        decimal secondsPerUnit,
        MachineId? machineId) => new()
    {
        VersionId = versionId,
        CompanyId = new("company-1"),
        SiteId = new("SITE-1"),
        PartId = new("part-1"),
        OperationId = new("operation-1"),
        MachineId = machineId,
        SecondsPerUnit = secondsPerUnit,
        EffectiveFromUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        SourceReference = $"approval:{versionId}",
        PublishedRevision = revision,
    };
}
