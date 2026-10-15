using System.Data;
using FactoryConnect.Core.Oee;
using FactoryConnect.Core.Quality;
using FactoryConnect.Persistence.SqlServer;
using FactoryConnect.Reporting.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerDerivedOeeAssessmentIntegrationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RoundTripRestartAndReplayRetainValueReasonsAndProvenance(bool measured, bool unavailable, bool day)
    {
        await using var database = await CreateAsync();
        var quality = DerivedOeeTestData.Quality(measured: measured, unavailable: unavailable, day: day);
        await new SqlServerQualityAssessmentStore(database.ConnectionString).PublishAsync(quality, CancellationToken.None);
        var target = DerivedOeeTestData.Target(quality); var assessment = DerivedOeeAssessor.Assess(target);
        var store = new SqlServerDerivedOeeAssessmentStore(database.ConnectionString);
        Assert.Equal(QualityPublicationOutcome.Appended, await store.PublishAsync(assessment, CancellationToken.None));
        var restarted = new SqlServerDerivedOeeAssessmentStore(database.ConnectionString);
        Assert.Equal(QualityPublicationOutcome.Replayed, await restarted.PublishAsync(assessment, CancellationToken.None));
        var read = await restarted.ReadByIdentityAsync(assessment.Identity, CancellationToken.None);
        Assert.Equal(QualityStorageReadOutcome.Found, read.Outcome);
        Assert.Equal(DerivedOeeStorageV1Codec.Encode(assessment), DerivedOeeStorageV1Codec.Encode(read.Assessment!));
        Assert.Equal(assessment.IsProvisional, read.Assessment!.IsProvisional);
        Assert.Equal(assessment.AssumedPolicy?.ApprovalReference, read.Assessment.AssumedPolicy?.ApprovalReference);
        Assert.Equal(assessment.Identity, (await restarted.ReadExactAsync(target, CancellationToken.None)).Assessment?.Identity);
    }

    [Fact]
    public async Task ChangedDependenciesNeverFallBackAndHistoricalTargetsRemainRetrievable()
    {
        await using var database = await CreateAsync();
        var qualityStore = new SqlServerQualityAssessmentStore(database.ConnectionString);
        var store = new SqlServerDerivedOeeAssessmentStore(database.ConnectionString);
        var original = DerivedOeeTestData.Target();
        await qualityStore.PublishAsync(original.Quality, CancellationToken.None);
        await store.PublishAsync(DerivedOeeAssessor.Assess(original), CancellationToken.None);
        foreach (var target in new[] { DerivedOeeTestData.Target(metricRevision: 2), DerivedOeeTestData.Target(cutRevision: "2"),
            DerivedOeeTestData.Target(DerivedOeeTestData.Quality(measured: true)) })
        {
            Assert.Equal(QualityStorageReadOutcome.Absent, (await store.ReadExactAsync(target, CancellationToken.None)).Outcome);
            await qualityStore.PublishAsync(target.Quality, CancellationToken.None);
            await store.PublishAsync(DerivedOeeAssessor.Assess(target), CancellationToken.None);
            Assert.Equal(DerivedOeeIdentity.Create(target), (await store.ReadExactAsync(target, CancellationToken.None)).Assessment?.Identity);
        }
        Assert.Equal(DerivedOeeIdentity.Create(original), (await store.ReadExactAsync(original, CancellationToken.None)).Assessment?.Identity);
        Assert.Equal(QualityStorageReadOutcome.IncomparableEvidence, (await store.ReadExactAsync(null, CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadExactAsync(DerivedOeeTestData.Target(ideal: 61), CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentIdenticalAndConflictingPublicationsHaveOneImmutableWinner(bool conflict)
    {
        await using var database = await CreateAsync();
        var first = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target());
        var second = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(ideal: conflict ? 61 : 60));
        await new SqlServerQualityAssessmentStore(database.ConnectionString).PublishAsync(first.Target.Quality, CancellationToken.None);
        async Task<string> Publish(DerivedOeeAssessment value)
        {
            try { return (await new SqlServerDerivedOeeAssessmentStore(database.ConnectionString).PublishAsync(value, CancellationToken.None)).ToString(); }
            catch (InvalidDataException) { return "Conflict"; }
        }
        var outcomes = await Task.WhenAll(Publish(first), Publish(second));
        Assert.Contains("Appended", outcomes); Assert.Contains(conflict ? "Conflict" : "Replayed", outcomes);
        var stored = (await new SqlServerDerivedOeeAssessmentStore(database.ConnectionString).ReadByIdentityAsync(first.Identity, CancellationToken.None)).Assessment!;
        Assert.Contains(stored.ContentFingerprint, new[] { first.ContentFingerprint, second.ContentFingerprint });
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.DerivedOeeAssessment;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("PayloadBinary = 0x01", QualityStorageReadOutcome.Corrupt)]
    [InlineData("IdentityBinary = 0x01", QualityStorageReadOutcome.Corrupt)]
    [InlineData("PayloadCodecVersion = 99", QualityStorageReadOutcome.UnsupportedEncoding)]
    public async Task CorruptOrUnsupportedRowsCannotBeReadOrReplayed(string corruption, QualityStorageReadOutcome expected)
    {
        await using var database = await CreateAsync();
        var assessment = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target());
        await new SqlServerQualityAssessmentStore(database.ConnectionString).PublishAsync(assessment.Target.Quality, CancellationToken.None);
        var store = new SqlServerDerivedOeeAssessmentStore(database.ConnectionString); await store.PublishAsync(assessment, CancellationToken.None);
        await ExecuteAsync(database, "UPDATE dbo.DerivedOeeAssessment SET " + corruption + ";");
        Assert.Equal(expected, (await store.ReadByIdentityAsync(assessment.Identity, CancellationToken.None)).Outcome);
        if (expected == QualityStorageReadOutcome.UnsupportedEncoding)
            await Assert.ThrowsAsync<NotSupportedException>(() => store.PublishAsync(assessment, CancellationToken.None).AsTask());
        else await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(assessment, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task QualityMustBeDurableAndRemainExactAndValid()
    {
        await using var database = await CreateAsync();
        var target = DerivedOeeTestData.Target(); var assessment = DerivedOeeAssessor.Assess(target);
        var store = new SqlServerDerivedOeeAssessmentStore(database.ConnectionString);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(assessment, CancellationToken.None).AsTask());
        Assert.Equal(QualityStorageReadOutcome.Absent, (await store.ReadByIdentityAsync(assessment.Identity, CancellationToken.None)).Outcome);
        await new SqlServerQualityAssessmentStore(database.ConnectionString).PublishAsync(target.Quality, CancellationToken.None);
        await store.PublishAsync(assessment, CancellationToken.None);
        await ExecuteAsync(database, "UPDATE dbo.QualityAssessment SET ContentHash = 0x01;");
        Assert.Equal(QualityStorageReadOutcome.Corrupt, (await store.ReadByIdentityAsync(assessment.Identity, CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(assessment, CancellationToken.None).AsTask());
    }

    private static async Task<SqlStartupIsolatedDatabase> CreateAsync()
    {
        var database = await SqlStartupIsolatedDatabase.CreateAsync();
        try
        {
            await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
            await new SqlServerMigrationEngine(SqlMigrationCatalog.Load(), new Clock()).ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
    private static async Task ExecuteAsync(SqlStartupIsolatedDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private sealed class Clock : ISqlMigrationUtcClock { public DateTimeOffset UtcNow => DerivedOeeTestData.Start; }
}

public sealed class DerivedOeeStorageCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadRoundTripAndCorruptionChecksAreDeterministic(bool measured)
    {
        var original = DerivedOeeAssessor.Assess(DerivedOeeTestData.Target(DerivedOeeTestData.Quality(measured: measured)));
        var bytes = DerivedOeeStorageV1Codec.Encode(original); var decoded = DerivedOeeStorageV1Codec.Decode(bytes);
        Assert.Equal(bytes, DerivedOeeStorageV1Codec.Encode(decoded));
        Assert.Equal(original.Identity, decoded.Identity); Assert.Equal(original.ContentFingerprint, decoded.ContentFingerprint);
        Assert.Throws<InvalidDataException>(() => DerivedOeeStorageV1Codec.Decode(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => DerivedOeeStorageV1Codec.Decode([.. bytes, 0]));
        bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => DerivedOeeStorageV1Codec.Decode(bytes));
    }
}
