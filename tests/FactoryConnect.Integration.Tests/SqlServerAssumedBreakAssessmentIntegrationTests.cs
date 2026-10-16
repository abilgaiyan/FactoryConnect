using System.Data;
using System.Security.Cryptography;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAssumedBreakAssessmentIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RoundTripRestartAndReplayPreserveMissingZeroAndStage(bool missing, bool forecast)
    {
        await using var db = await CreateAsync();
        var value = Assessment(missing, forecast);
        var store = new SqlServerAssumedBreakAssessmentStore(db.ConnectionString);
        Assert.Equal(AssumedBreakPublicationOutcome.Appended, await store.PublishAsync(value, CancellationToken.None));
        var restarted = new SqlServerAssumedBreakAssessmentStore(db.ConnectionString);
        Assert.Equal(AssumedBreakPublicationOutcome.Replayed, await restarted.PublishAsync(value, CancellationToken.None));
        var read = await restarted.ReadExactAsync(value.Target, CancellationToken.None);
        Assert.Equal(AssumedBreakStorageReadOutcome.Found, read.Outcome);
        Assert.Equal(value, read.Assessment);
        Assert.Equal(AssumedBreakAssessmentV1Codec.Encode(value), AssumedBreakAssessmentV1Codec.Encode(read.Assessment!));
    }

    [Fact]
    public async Task ExactTargetsNeverFallBackAndHistoricalStagesRemainReadable()
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAssessmentStore(db.ConnectionString);
        var original = Assessment();
        await store.PublishAsync(original, CancellationToken.None);
        foreach (var changed in new[]
        {
            original.Target with { ScheduleVersion = "schedule-v2" },
            original.Target with { PolicyVersion = "policy-v2" },
            original.Target with { AggregationRevision = new(original.Target.AggregationRevision.ProcessorId,
                original.Target.AggregationRevision.StreamId, new MetricInputPosition(535)) },
            Assessment(forecast: true).Target,
            Assessment(missing: true).Target,
        })
            Assert.Equal(AssumedBreakStorageReadOutcome.Absent, (await store.ReadExactAsync(changed, CancellationToken.None)).Outcome);
        var forecast = Assessment(forecast: true);
        await store.PublishAsync(forecast, CancellationToken.None);
        Assert.Equal(original, (await store.ReadByIdentityAsync(original.Identity, CancellationToken.None)).Assessment);
        Assert.Equal(forecast, (await store.ReadExactAsync(forecast.Target, CancellationToken.None)).Assessment);
        Assert.Equal(AssumedBreakStorageReadOutcome.IncomparableEvidence, (await store.ReadExactAsync(null, CancellationToken.None)).Outcome);
        Assert.Equal(AssumedBreakStorageReadOutcome.IncomparableEvidence,
            (await store.ReadExactAsync(original.Target with { ScheduleVersion = "" }, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPublicationsHaveExactlyOneImmutableWinner(bool conflict)
    {
        await using var db = await CreateAsync();
        var first = Assessment();
        var second = first with { ObservedRunningDuration = conflict ? TimeSpan.FromMinutes(1) : TimeSpan.Zero };
        async Task<string> Publish(DurableAssumedBreakAssessment value)
        {
            try { return (await new SqlServerAssumedBreakAssessmentStore(db.ConnectionString).PublishAsync(value, CancellationToken.None)).ToString(); }
            catch (InvalidDataException) { return "Conflict"; }
        }
        var results = await Task.WhenAll(Publish(first), Publish(second));
        Assert.Single(results, x => x == "Appended");
        Assert.Contains(conflict ? "Conflict" : "Replayed", results);
        var store = new SqlServerAssumedBreakAssessmentStore(db.ConnectionString);
        var winner = (await store.ReadExactAsync(first.Target, CancellationToken.None)).Assessment!;
        Assert.Contains(winner, new[] { first, second });
        var loser = winner == first ? second : first;
        if (conflict)
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(loser, CancellationToken.None).AsTask());
        Assert.Equal(winner, (await store.ReadExactAsync(first.Target, CancellationToken.None)).Assessment);
        await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.AssumedBreakAssessment;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("PayloadBinary = 0x01", AssumedBreakStorageReadOutcome.Corrupt)]
    [InlineData("IdentityBinary = 0x01", AssumedBreakStorageReadOutcome.Corrupt)]
    [InlineData("ContentHash = 0x01", AssumedBreakStorageReadOutcome.Corrupt)]
    [InlineData("PayloadCodecVersion = 99", AssumedBreakStorageReadOutcome.UnsupportedEncoding)]
    [InlineData("IdentityCodecVersion = 99", AssumedBreakStorageReadOutcome.UnsupportedEncoding)]
    public async Task CorruptionAndUnknownVersionsCannotBeReadOrReplayed(string mutation, AssumedBreakStorageReadOutcome expected)
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAssessmentStore(db.ConnectionString);
        var value = Assessment(); await store.PublishAsync(value, CancellationToken.None);
        await ExecuteAsync(db, "UPDATE dbo.AssumedBreakAssessment SET " + mutation + ";");
        Assert.Equal(expected, (await store.ReadExactAsync(value.Target, CancellationToken.None)).Outcome);
        if (expected == AssumedBreakStorageReadOutcome.UnsupportedEncoding)
            await Assert.ThrowsAsync<NotSupportedException>(() => store.PublishAsync(value, CancellationToken.None).AsTask());
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(value, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidHashesDoNotHideMalformedPayloadOrDecodedIdentityMismatch(bool malformed)
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAssessmentStore(db.ConnectionString);
        var value = Assessment(); await store.PublishAsync(value, CancellationToken.None);
        var other = value with { Target = value.Target with { ScheduleVersion = "other" } };
        var payload = malformed ? new byte[] { 1, 0, 0, 0 } : AssumedBreakAssessmentV1Codec.Encode(other);
        await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.AssumedBreakAssessment SET PayloadBinary = @Payload, ContentHash = @Hash;";
        command.Parameters.Add("@Payload", SqlDbType.VarBinary, -1).Value = payload;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = SHA256.HashData(payload);
        await command.ExecuteNonQueryAsync();
        Assert.Equal(AssumedBreakStorageReadOutcome.Corrupt, (await store.ReadExactAsync(value.Target, CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(value, CancellationToken.None).AsTask());
    }

    private static DurableAssumedBreakAssessment Assessment(bool missing = false, bool forecast = false)
    {
        var target = new AssumedBreakAssessmentTarget(new SiteId("GG-SITE"), Machine, new DateOnly(2026, 10, 16),
            Start, Start.AddHours(8), "SHIFT-1", "schedule-v1", "calendar-v1", "break-rule-v1",
            "DemoProvisional", forecast ? "Forecast" : "Completed", "POLICY", "policy-v1",
            new DateOnly(2026, 10, 16), null, TimeSpan.FromMinutes(30), "demo instruction",
            new(new MetricAggregationProcessorId("metric-aggregation-v1"), MetricInputStreamId.ForMachine(Machine), new MetricInputPosition(534)),
            missing ? null : "RUN-1", new string('A', 64));
        return new(target, new(TimeSpan.FromHours(8), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(450), forecast, true),
            missing ? null : TimeSpan.Zero,
            forecast ? "BreakTimingNotEstablished" : missing ? "RunningEvidenceNotEstablished" : "BreakAttributionNotEstablished", null);
    }
    private static async Task<SqlStartupIsolatedDatabase> CreateAsync()
    {
        var db = await SqlStartupIsolatedDatabase.CreateAsync();
        try
        {
            await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
            await new SqlServerMigrationEngine(SqlMigrationCatalog.Load(), new Clock()).ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static async Task ExecuteAsync(SqlStartupIsolatedDatabase db, string sql)
    {
        await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private sealed class Clock : ISqlMigrationUtcClock { public DateTimeOffset UtcNow => Start; }
}
