using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricCoverageAssessmentIntegrationTests
{
    [Fact]
    public async Task AppendHistoricalReplayAndConflictsPreserveHistory()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        var first = Request(1);
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Appended, await store.PublishAsync(first, CancellationToken.None));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Appended, await store.PublishAsync(Request(2), CancellationToken.None));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Appended, await store.PublishAsync(Request(3), CancellationToken.None));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Replayed, await store.PublishAsync(Request(2), CancellationToken.None));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.ContentConflict, await store.PublishAsync(Request(2, changed: true), CancellationToken.None));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.PredecessorConflict, await store.PublishAsync(Request(5), CancellationToken.None));
        var found = await store.ReadExactAsync(first.Subject, new(1), CancellationToken.None);
        Assert.Equal(OperationalMetricCoverageReadOutcome.Found, found.Outcome);
        Assert.Equal(first.Assessment, found.Assessment);
        Assert.Equal(OperationalMetricCoverageReadOutcome.Absent, (await store.ReadExactAsync(first.Subject, new(4), CancellationToken.None)).Outcome);
        Assert.Equal("3:3", await StateAsync(database.ConnectionString));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentFirstPublicationReconcilesOrConflicts(bool changed)
    {
        await using var database = await CreateAsync();
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        var results = await Task.WhenAll(store.PublishAsync(Request(1), CancellationToken.None).AsTask(),
            store.PublishAsync(Request(1, changed), CancellationToken.None).AsTask());
        Assert.Contains(OperationalMetricCoveragePublicationOutcome.Appended, results);
        Assert.Contains(changed ? OperationalMetricCoveragePublicationOutcome.ContentConflict : OperationalMetricCoveragePublicationOutcome.Replayed, results);
        Assert.Equal("1:1", await StateAsync(database.ConnectionString));
    }

    [Theory]
    [InlineData("SubjectInserted")]
    [InlineData("VersionInserted")]
    [InlineData("HeadAdvanced")]
    public async Task EveryInitialWriteStageRollsBack(string stage)
    {
        await using var database = await CreateAsync();
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString,
            OperationalMetricCoverageV1Codec.Hash, current => { if (current == stage) { throw new IOException("Injected rollback"); } });
        await Assert.ThrowsAsync<IOException>(() => store.PublishAsync(Request(1), CancellationToken.None).AsTask());
        Assert.Equal("0:0", await StateAsync(database.ConnectionString));
        Assert.Equal(0L, await ScalarAsync(database.ConnectionString, "SELECT COUNT_BIG(*) FROM dbo.OperationalMetricCoverageSubject;"));
    }

    [Theory]
    [InlineData("VersionInserted")]
    [InlineData("HeadAdvanced")]
    public async Task EveryReassessmentWriteStageRollsBack(string stage)
    {
        await using var database = await CreateAsync();
        var normal = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        await normal.PublishAsync(Request(1), CancellationToken.None);
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString,
            OperationalMetricCoverageV1Codec.Hash, current => { if (current == stage) { throw new IOException("Injected rollback"); } });
        await Assert.ThrowsAsync<IOException>(() => store.PublishAsync(Request(2), CancellationToken.None).AsTask());
        Assert.Equal("1:1", await StateAsync(database.ConnectionString));
    }

    [Fact]
    public async Task LostAcknowledgementRecoversOriginalIdentity()
    {
        await using var database = await CreateAsync();
        var uncertain = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString,
            OperationalMetricCoverageV1Codec.Hash, stage => { if (stage == "CommitAcknowledged") { throw new IOException("Lost response"); } });
        var request = Request(1);
        await Assert.ThrowsAsync<IOException>(() => uncertain.PublishAsync(request, CancellationToken.None).AsTask());
        var normal = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        Assert.Equal(request.Assessment, (await normal.ReadExactAsync(request.Subject, request.ProposedRevision, CancellationToken.None)).Assessment);
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Replayed, await normal.PublishAsync(request, CancellationToken.None));
        Assert.Equal("1:1", await StateAsync(database.ConnectionString));
    }

    [Fact]
    public async Task SharedReadWaitsUntilAppendCommits()
    {
        await using var database = await CreateAsync();
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var request = Request(1);
        var writer = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString,
            OperationalMetricCoverageV1Codec.Hash, stage =>
            {
                if (stage == "VersionInserted")
                {
                    reached.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20))) { throw new TimeoutException("Test release timeout"); }
                }
            });
        var write = Task.Run(async () => await writer.PublishAsync(request, CancellationToken.None));
        Assert.True(reached.Wait(TimeSpan.FromSeconds(20)));
        var reader = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        var read = reader.ReadExactAsync(request.Subject, new(1), CancellationToken.None).AsTask();
        try
        {
            Assert.NotSame(read, await Task.WhenAny(read, Task.Delay(200)));
        }
        finally { release.Set(); }
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Appended, await write);
        Assert.Equal(OperationalMetricCoverageReadOutcome.Found, (await read).Outcome);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.OperationalMetricCoverageHead;", OperationalMetricCoverageReadOutcome.Corrupt)]
    [InlineData("UPDATE dbo.OperationalMetricCoverageHead SET HeadRevision = 1;", OperationalMetricCoverageReadOutcome.Corrupt)]
    [InlineData("UPDATE dbo.OperationalMetricCoverageSubject SET IdentityCodecVersion = 99;", OperationalMetricCoverageReadOutcome.UnsupportedEncoding)]
    [InlineData("UPDATE dbo.OperationalMetricCoverageVersion SET ContentCodecVersion = 99 WHERE AssessmentRevision = 1;", OperationalMetricCoverageReadOutcome.UnsupportedEncoding)]
    [InlineData("UPDATE dbo.OperationalMetricCoverageVersion SET ContentBinary = 0x01 WHERE AssessmentRevision = 1;", OperationalMetricCoverageReadOutcome.Corrupt)]
    [InlineData("UPDATE dbo.OperationalMetricCoverageSubject SET SubjectBinary = 0x01;", OperationalMetricCoverageReadOutcome.Corrupt)]
    [InlineData("ALTER TABLE dbo.OperationalMetricCoverageHead NOCHECK CONSTRAINT ALL; UPDATE dbo.OperationalMetricCoverageHead SET HeadRevision = 9;", OperationalMetricCoverageReadOutcome.Corrupt)]
    [InlineData("ALTER TABLE dbo.OperationalMetricCoverageHead NOCHECK CONSTRAINT ALL; DELETE FROM dbo.OperationalMetricCoverageVersion WHERE AssessmentRevision = 1;", OperationalMetricCoverageReadOutcome.Corrupt)]
    [InlineData("ALTER TABLE dbo.OperationalMetricCoverageSubject NOCHECK CONSTRAINT ALL; UPDATE dbo.OperationalMetricCoverageSubject SET MetricInputStreamRowId = 999;", OperationalMetricCoverageReadOutcome.Corrupt)]
    public async Task IntegrityAndEncodingFailuresPrecedeReplayAndAbsence(string corruption, OperationalMetricCoverageReadOutcome expected)
    {
        await using var database = await CreateAsync();
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        await store.PublishAsync(Request(1), CancellationToken.None);
        await store.PublishAsync(Request(2), CancellationToken.None);
        await ExecuteAsync(database.ConnectionString, corruption);
        var before = await StateAsync(database.ConnectionString);
        var request = Request(2);
        Assert.Equal(expected, (await store.ReadExactAsync(request.Subject, new(3), CancellationToken.None)).Outcome);
        Assert.Equal(expected == OperationalMetricCoverageReadOutcome.UnsupportedEncoding
            ? OperationalMetricCoveragePublicationOutcome.UnsupportedEncoding : OperationalMetricCoveragePublicationOutcome.IntegrityFailure,
            await store.PublishAsync(request, CancellationToken.None));
        Assert.Equal(before, await StateAsync(database.ConnectionString));
    }

    [Fact]
    public async Task AbsentSourceAndUnsignedMaximumAreExact()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.SourceRevisionAbsent, await store.PublishAsync(Request(1, position: 3244), CancellationToken.None));
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.Appended, await store.PublishAsync(Request(1, position: ulong.MaxValue), CancellationToken.None));
        Assert.Equal(ulong.MaxValue, (await store.ReadExactAsync(Request(1, position: ulong.MaxValue).Subject, new(1), CancellationToken.None)).Assessment!.SourceRevision.Position.Value);
    }

    [Fact]
    public async Task DetectedHashCollisionDoesNotWrite()
    {
        await using var database = await CreateAsync();
        byte[] Hash(byte[] _) => new byte[32];
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString, Hash, null);
        await store.PublishAsync(Request(1), CancellationToken.None);
        var assessment = CoverageTestData.Assessment(policy: "different");
        var request = new OperationalMetricCoveragePublicationRequest(OperationalMetricCoverageSubject.FromAssessment(assessment), new(1), null, assessment);
        Assert.Equal(OperationalMetricCoveragePublicationOutcome.IntegrityFailure, await store.PublishAsync(request, CancellationToken.None));
        Assert.Equal("1:1", await StateAsync(database.ConnectionString));
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAnyWrite()
    {
        await using var database = await CreateAsync();
        var store = new SqlServerOperationalMetricCoverageAssessmentStore(database.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishAsync(Request(1), cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadExactAsync(Request(1).Subject, new(1), cancellation.Token).AsTask());
        Assert.Equal("0:0", await StateAsync(database.ConnectionString));
    }

    private static OperationalMetricCoveragePublicationRequest Request(long revision, bool changed = false, ulong position = 3243)
    {
        var assessment = CoverageTestData.Assessment(references: changed ? [new("raw", "changed", "1")] : null, position: position);
        return new(OperationalMetricCoverageSubject.FromAssessment(assessment), new(revision), revision == 1 ? null : new(revision - 1), assessment);
    }

    private static async Task<SqlStartupIsolatedDatabase> CreateAsync()
    {
        var database = await SqlStartupIsolatedDatabase.CreateAsync();
        try
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await new SqlServerMigrationEngine(SqlMigrationCatalog.Load(), new Clock()).ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dbo.MetricInputStream (MachineId, StreamKeyBinary, StreamKey) VALUES (@Machine, @Stream, N'metrics');
                DECLARE @StreamId bigint = SCOPE_IDENTITY();
                INSERT INTO dbo.MetricAggregationProcessor (ProcessorKeyBinary, ProcessorKey, MetricInputStreamRowId) VALUES (@Processor, N'aggregation', @StreamId);
                DECLARE @ProcessorId bigint = SCOPE_IDENTITY();
                INSERT INTO dbo.MetricAggregationRevision (MetricAggregationProcessorRowId, Position) VALUES (@ProcessorId, 3243), (@ProcessorId, 18446744073709551615);
                """;
            command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = CoverageTestData.Machine.Value;
            command.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode("metrics");
            command.Parameters.Add("@Processor", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode("aggregation");
            await command.ExecuteNonQueryAsync();
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string> StateAsync(string connectionString) => (string)(await ScalarAsync(connectionString, """
        SELECT CONCAT((SELECT COUNT_BIG(*) FROM dbo.OperationalMetricCoverageVersion), ':',
            COALESCE((SELECT MAX(HeadRevision) FROM dbo.OperationalMetricCoverageHead), 0));
        """))!;
    private sealed class Clock : ISqlMigrationUtcClock
    {
        public DateTimeOffset UtcNow => CoverageTestData.Start;
    }
}
