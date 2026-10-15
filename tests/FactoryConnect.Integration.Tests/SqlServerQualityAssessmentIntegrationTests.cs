using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Quality;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerQualityAssessmentIntegrationTests
{
    [Fact]
    public async Task RoundTripRestartReplayAndChangedTargetNeverFallBack()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerQualityAssessmentStore(database.ConnectionString);
        var target = QualitySqlTestData.Target(); var first = QualityAssessor.Assess(target);
        Assert.Equal(QualityStorageReadOutcome.Absent, (await store.ReadExactAsync(target, CancellationToken.None)).Outcome);
        Assert.Equal(QualityPublicationOutcome.Appended, await store.PublishAsync(first, CancellationToken.None));
        var restarted = new SqlServerQualityAssessmentStore(database.ConnectionString);
        Assert.Equal(QualityPublicationOutcome.Replayed, await restarted.PublishAsync(first, CancellationToken.None));
        var found = await restarted.ReadByIdentityAsync(first.Identity, CancellationToken.None);
        Assert.Equal(QualityStorageReadOutcome.Found, found.Outcome);
        Assert.Equal(QualityStorageV1Codec.EncodeAssessment(first), QualityStorageV1Codec.EncodeAssessment(found.Assessment!));
        Assert.Equal(QualityStorageReadOutcome.Found, (await restarted.ReadPolicyAsync(QualitySqlTestData.Policy().Identity, CancellationToken.None)).Outcome);
        Assert.Equal(QualityStorageReadOutcome.Absent, (await restarted.ReadPolicyAsync(new("missing", "1"), CancellationToken.None)).Outcome);
        foreach (var changed in new[] { QualitySqlTestData.Target("2"), QualitySqlTestData.Target(policyVersion: "2"), QualitySqlTestData.Target(measured: true) })
        {
            Assert.Equal(QualityStorageReadOutcome.Absent, (await restarted.ReadExactAsync(changed, CancellationToken.None)).Outcome);
            var assessment = QualityAssessor.Assess(changed);
            Assert.Equal(QualityPublicationOutcome.Appended, await restarted.PublishAsync(assessment, CancellationToken.None));
            Assert.Equal(assessment.Identity, (await restarted.ReadExactAsync(changed, CancellationToken.None)).Assessment?.Identity);
        }
        Assert.Equal(first.Identity, (await restarted.ReadExactAsync(target, CancellationToken.None)).Assessment?.Identity);
        Assert.Equal(QualityStorageReadOutcome.IncomparableEvidence, (await restarted.ReadExactAsync(null, CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ReadExactAsync(QualitySqlTestData.Target(produced: 101), CancellationToken.None).AsTask());
        Assert.Equal(4L, await CountAsync(database, "QualityAssessment"));
        Assert.Equal(2L, await CountAsync(database, "QualityPolicy"));
    }

    [Fact]
    public async Task MeasuredZeroDayAndUnavailableResultsRoundTrip()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerQualityAssessmentStore(database.ConnectionString);
        foreach (var target in new[] { QualitySqlTestData.Target(measured: true), QualitySqlTestData.Target(produced: 0, day: true),
            QualitySqlTestData.Target(measurementState: QualityMeasurementState.Partial) })
        {
            var assessment = QualityAssessor.Assess(target);
            await store.PublishAsync(assessment, CancellationToken.None);
            var decoded = (await store.ReadExactAsync(target, CancellationToken.None)).Assessment!;
            Assert.Equal(assessment.Value, decoded.Value); Assert.Equal(assessment.Basis, decoded.Basis);
            Assert.Equal(assessment.Reason, decoded.Reason);
        }
    }

    [Fact]
    public async Task IndependentPolicyPublicationReplaysAndRejectsConflictingApproval()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerQualityAssessmentStore(database.ConnectionString);
        Assert.Equal(QualityPublicationOutcome.Appended, await store.PublishPolicyAsync(QualitySqlTestData.Policy(), CancellationToken.None));
        Assert.Equal(QualityPublicationOutcome.Replayed, await store.PublishPolicyAsync(QualitySqlTestData.Policy(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishPolicyAsync(QualitySqlTestData.Policy(approval: "changed"), CancellationToken.None).AsTask());
        var read = await store.ReadPolicyAsync(QualitySqlTestData.Policy().Identity, CancellationToken.None);
        Assert.Equal("approval", read.Policy?.ApprovalReference);
        Assert.Equal(1L, await CountAsync(database, "QualityPolicy"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentIdenticalOrConflictingAssessmentsKeepOneImmutableWinner(bool conflict)
    {
        await using var database = await CreateAsync();
        var first = QualityAssessor.Assess(QualitySqlTestData.Target());
        var second = QualityAssessor.Assess(QualitySqlTestData.Target(produced: conflict ? 101 : 100));
        async Task<string> Publish(QualityAssessment value)
        {
            try { return (await new SqlServerQualityAssessmentStore(database.ConnectionString).PublishAsync(value, CancellationToken.None)).ToString(); }
            catch (InvalidDataException) { return "Conflict"; }
        }
        var outcomes = await Task.WhenAll(Publish(first), Publish(second));
        Assert.Contains("Appended", outcomes); Assert.Contains(conflict ? "Conflict" : "Replayed", outcomes);
        Assert.Equal(1L, await CountAsync(database, "QualityAssessment")); Assert.Equal(1L, await CountAsync(database, "QualityPolicy"));
        var winner = (await new SqlServerQualityAssessmentStore(database.ConnectionString).ReadByIdentityAsync(first.Identity, CancellationToken.None)).Assessment!;
        Assert.Contains(winner.ContentFingerprint, new[] { first.ContentFingerprint, second.ContentFingerprint });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPolicyPublicationKeepsOneImmutableWinner(bool conflict)
    {
        await using var database = await CreateAsync();
        async Task<string> Publish(string approval)
        {
            try { return (await new SqlServerQualityAssessmentStore(database.ConnectionString).PublishPolicyAsync(QualitySqlTestData.Policy(approval: approval), CancellationToken.None)).ToString(); }
            catch (InvalidDataException) { return "Conflict"; }
        }
        var results = await Task.WhenAll(Publish("approval"), Publish(conflict ? "different" : "approval"));
        Assert.Contains("Appended", results); Assert.Contains(conflict ? "Conflict" : "Replayed", results);
        Assert.Equal(1L, await CountAsync(database, "QualityPolicy"));
    }

    [Theory]
    [InlineData("PolicyInserted")]
    [InlineData("AssessmentInserted")]
    public async Task FailedPublicationRollsBackAllPolicyAndAssessmentRows(string failedStage)
    {
        await using var database = await CreateAsync();
        var store = new SqlServerQualityAssessmentStore(database.ConnectionString, stage => { if (stage == failedStage) throw new IOException("Injected failure"); });
        await Assert.ThrowsAsync<IOException>(() => store.PublishAsync(QualityAssessor.Assess(QualitySqlTestData.Target()), CancellationToken.None).AsTask());
        Assert.Equal(0L, await CountAsync(database, "QualityPolicy")); Assert.Equal(0L, await CountAsync(database, "QualityAssessment"));
    }

    [Fact]
    public async Task ConflictingPolicyDoesNotPublishAssessmentOrOtherNewPolicy()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerQualityAssessmentStore(database.ConnectionString);
        await store.PublishPolicyAsync(QualitySqlTestData.Policy(approval: "different"), CancellationToken.None);
        var original = QualitySqlTestData.Target();
        var target = new QualityAssessmentTarget(original.Scope, original.Production, original.Measurement,
            [QualitySqlTestData.Policy(), QualitySqlTestData.Policy("2")], original.RuleVersion);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(QualityAssessor.Assess(target), CancellationToken.None).AsTask());
        Assert.Equal(1L, await CountAsync(database, "QualityPolicy")); Assert.Equal(0L, await CountAsync(database, "QualityAssessment"));
    }

    [Fact]
    public async Task LostAcknowledgementCanBeReadAndReplayedAfterRestart()
    {
        await using var database = await CreateAsync();
        var assessment = QualityAssessor.Assess(QualitySqlTestData.Target());
        var uncertain = new SqlServerQualityAssessmentStore(database.ConnectionString, stage => { if (stage == "CommitAcknowledged") throw new IOException("Lost acknowledgement"); });
        await Assert.ThrowsAsync<IOException>(() => uncertain.PublishAsync(assessment, CancellationToken.None).AsTask());
        var restarted = new SqlServerQualityAssessmentStore(database.ConnectionString);
        Assert.Equal(QualityStorageReadOutcome.Found, (await restarted.ReadByIdentityAsync(assessment.Identity, CancellationToken.None)).Outcome);
        Assert.Equal(QualityPublicationOutcome.Replayed, await restarted.PublishAsync(assessment, CancellationToken.None));
    }

    [Theory]
    [InlineData("QualityAssessment", "PayloadBinary = 0x01", QualityStorageReadOutcome.Corrupt)]
    [InlineData("QualityAssessment", "IdentityBinary = 0x01", QualityStorageReadOutcome.Corrupt)]
    [InlineData("QualityAssessment", "ContentHash = 0x01", QualityStorageReadOutcome.Corrupt)]
    [InlineData("QualityAssessment", "PayloadCodecVersion = 99", QualityStorageReadOutcome.UnsupportedEncoding)]
    [InlineData("QualityPolicy", "PayloadBinary = 0x01", QualityStorageReadOutcome.Corrupt)]
    [InlineData("QualityPolicy", "IdentityCodecVersion = 99", QualityStorageReadOutcome.UnsupportedEncoding)]
    public async Task CorruptionAndUnsupportedEncodingBlockReadAndReplay(string table, string corruption, QualityStorageReadOutcome expected)
    {
        await using var database = await CreateAsync();
        var store = new SqlServerQualityAssessmentStore(database.ConnectionString);
        var assessment = QualityAssessor.Assess(QualitySqlTestData.Target());
        await store.PublishAsync(assessment, CancellationToken.None);
        await ExecuteAsync(database, $"UPDATE dbo.{table} SET {corruption};");
        Assert.Equal(expected, (await store.ReadByIdentityAsync(assessment.Identity, CancellationToken.None)).Outcome);
        if (table == "QualityPolicy") Assert.Equal(expected, (await store.ReadPolicyAsync(QualitySqlTestData.Policy().Identity, CancellationToken.None)).Outcome);
        if (expected == QualityStorageReadOutcome.UnsupportedEncoding)
            await Assert.ThrowsAsync<NotSupportedException>(() => store.PublishAsync(assessment, CancellationToken.None).AsTask());
        else await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(assessment, CancellationToken.None).AsTask());
        Assert.Equal(1L, await CountAsync(database, "QualityAssessment")); Assert.Equal(1L, await CountAsync(database, "QualityPolicy"));
    }

    [Fact]
    public async Task ValidButMisboundPayloadAndMissingPolicyAreDetected()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerQualityAssessmentStore(database.ConnectionString);
        var first = QualityAssessor.Assess(QualitySqlTestData.Target());
        await store.PublishAsync(first, CancellationToken.None);
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.QualityAssessment SET PayloadBinary = @Payload, ContentHash = HASHBYTES('SHA2_256', @Payload);";
            command.Parameters.Add("@Payload", SqlDbType.VarBinary, -1).Value = QualityStorageV1Codec.EncodeAssessment(QualityAssessor.Assess(QualitySqlTestData.Target("other")));
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(QualityStorageReadOutcome.Corrupt, (await store.ReadByIdentityAsync(first.Identity, CancellationToken.None)).Outcome);
        await ExecuteAsync(database, "DELETE FROM dbo.QualityAssessment; DELETE FROM dbo.QualityPolicy;");
        await store.PublishAsync(first, CancellationToken.None);
        await ExecuteAsync(database, "DELETE FROM dbo.QualityPolicy;");
        Assert.Equal(QualityStorageReadOutcome.Corrupt, (await store.ReadByIdentityAsync(first.Identity, CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(first, CancellationToken.None).AsTask());
        Assert.Equal(0L, await CountAsync(database, "QualityPolicy"));
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
    private static async Task<long> CountAsync(SqlStartupIsolatedDatabase database, string table)
    {
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{table};";
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private static async Task ExecuteAsync(SqlStartupIsolatedDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private sealed class Clock : ISqlMigrationUtcClock { public DateTimeOffset UtcNow => QualitySqlTestData.Start; }
}
