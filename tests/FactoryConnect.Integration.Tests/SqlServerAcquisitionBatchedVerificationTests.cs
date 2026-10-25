using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionBatchedVerificationTests(SqlContainmentAuthorityDatabaseFixture fixture, ITestOutputHelper output)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Fact]
    public async Task WholePrefixAcrossChunkBoundariesUsesBoundedCommandsAndTheSameTransaction()
    {
        var history = await AcquisitionHistoryWorkload.SeedAsync(new(fixture.ConnectionString), 3, 257);
        var latest = history[^1];
        using var capture = new AcquisitionPersistenceCapture(fixture.ConnectionString);
        var store = new SqlServerObservationIngestionStore(capture.ConnectionString);
        var read = await store.ReadExactAsync(latest.Checkpoint.StreamId, Identity(latest));
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, read.Status);
        Assert.Equal(3, read.Record!.PublicationOrdinal);
        // 3 publication chunks each + 5 retained boundary checks; prefix SELECT is separate.
        Assert.Equal(14L, capture.SourceCommands);
        Assert.All(capture.SourceParameterCounts, count => Assert.InRange(count, 1, 131));
        Assert.All(capture.SourceTransactions, transaction => Assert.NotNull(transaction));
        Assert.Single(capture.SourceTransactions.Distinct());
        var span = Assert.Single(capture.Spans, activity => activity.OperationName == "acquisition.evidence.sources.verify");
        Assert.Equal(3, Convert.ToInt32(span.GetTagItem("publication.count"), CultureInfo.InvariantCulture));
        Assert.Equal(771L, Convert.ToInt64(span.GetTagItem("observation.count"), CultureInfo.InvariantCulture));
        Assert.Equal(14L, Convert.ToInt64(span.GetTagItem("sql.command.count"), CultureInfo.InvariantCulture));
        output.WriteLine("Observed source SQL commands={0}; legacy algorithm would issue 1547 for the same prefix; source stage={1:F3} ms",
            capture.SourceCommands, span.Duration.TotalMilliseconds);
        Assert.Null(capture.Error);
    }

    [Fact]
    public async Task BoundedKeysPreserveUnsignedInstanceAndSequenceValues()
    {
        var stream = new ObservationStreamId(MachineId.New(), "unsigned-sequence-batches");
        var first = (ulong)long.MaxValue + 3;
        var next = first + 130;
        var prototype = AcquisitionEvidenceCodecTests.Batch(stream).Observations[0].Observation;
        var observations = Enumerable.Range(0, 130).Select(index => new SequencedMachineObservation(first + (ulong)index,
            prototype with { Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index) })).ToArray();
        var batch = new ObservationIngestionBatch(null, new(stream, ulong.MaxValue, next), observations,
            DateTimeOffset.UnixEpoch, new(Guid.NewGuid(), first, first, next, []));
        await new SqlServerObservationIngestionStore(fixture.ConnectionString).CommitAsync(batch);
        using var capture = new AcquisitionPersistenceCapture(fixture.ConnectionString);
        var read = await new SqlServerObservationIngestionStore(capture.ConnectionString).ReadExactAsync(stream, Identity(batch));
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, read.Status);
        Assert.Equal(batch.Checkpoint, read.Record!.Batch.Checkpoint);
        Assert.Equal(3L, capture.SourceCommands);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("missing")]
    [InlineData("position-order")]
    public async Task CorruptionInTheOldestChunkRejectsLatestReadReplayAndAppendAtomically(string corruption)
    {
        var store = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var history = await AcquisitionHistoryWorkload.SeedAsync(store, 3, 257);
        var latest = history[^1];
        await using (var connection = fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var damage = connection.CreateCommand();
            BindStream(damage, latest.Checkpoint.StreamId);
            damage.CommandText = corruption switch
            {
                "value" => "UPDATE dbo.MachineObservation SET ObservationValue=N'CORRUPT' WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=2;",
                "missing" => "DELETE dbo.MachineObservation WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=2;",
                _ => "UPDATE dbo.MachineObservation SET Position=10000 WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=1; UPDATE dbo.MachineObservation SET Position=1 WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=2; UPDATE dbo.MachineObservation SET Position=2 WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=10000;"
            };
            await damage.ExecuteNonQueryAsync();
        }
        var damaged = await SnapshotAsync();
        Assert.Equal(AcquisitionEvidenceReadStatus.Corrupt, (await store.ReadExactAsync(latest.Checkpoint.StreamId, Identity(latest))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(latest).AsTask());
        var next = AcquisitionHistoryWorkload.Batch(latest.Checkpoint.StreamId, latest.Checkpoint, 129);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(next).AsTask());
        Assert.Equal(damaged, await SnapshotAsync());
    }

    [Theory]
    [InlineData("SignalType", "CK_MachineObservation_SignalType")]
    [InlineData("Quality", "CK_MachineObservation_Quality")]
    public async Task InvalidPersistedEnumsKeepExactReadAndCommitExceptionClassifications(string column, string constraint)
    {
        var store = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var history = await AcquisitionHistoryWorkload.SeedAsync(store, 2, 129);
        var latest = history[^1];
        var stream = latest.Checkpoint.StreamId;
        var original = column == "SignalType" ? (byte)history[0].Observations[1].Observation.Type : (byte)history[0].Observations[1].Observation.Quality;
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        try
        {
            // Only this disposable fixture is damaged. Restore both data and trusted constraint below.
            await using (var damage = connection.CreateCommand())
            {
                BindStream(damage, stream);
                damage.CommandText = $"ALTER TABLE dbo.MachineObservation NOCHECK CONSTRAINT {constraint}; UPDATE dbo.MachineObservation SET {column}=255 WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=2;";
                await damage.ExecuteNonQueryAsync();
            }
            var damaged = await SnapshotAsync();
            var read = await store.ReadExactAsync(stream, Identity(latest));
            Assert.Equal(AcquisitionEvidenceReadStatus.Corrupt, read.Status);
            Assert.Null(read.Record);
            var recovery = await store.ReadRecoveryExactAsync(stream, Identity(latest), "unselected-recovery");
            Assert.Equal(AcquisitionEvidenceReadStatus.Corrupt, recovery.Status);
            // Unlike a Boolean membership mismatch, enum materialization throws before ReadEvidenceAsync returns.
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(latest).AsTask());
            var next = AcquisitionHistoryWorkload.Batch(stream, latest.Checkpoint, 129);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(next).AsTask());
            Assert.Equal(damaged, await SnapshotAsync());
        }
        finally
        {
            await using var restore = connection.CreateCommand();
            BindStream(restore, stream);
            restore.Parameters.Add("@Original", System.Data.SqlDbType.TinyInt).Value = original;
            restore.CommandText = $"UPDATE dbo.MachineObservation SET {column}=@Original WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=2; ALTER TABLE dbo.MachineObservation WITH CHECK CHECK CONSTRAINT {constraint};";
            await restore.ExecuteNonQueryAsync();
        }
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await store.ReadExactAsync(stream, Identity(latest))).Status);
    }

    [Fact]
    public async Task ExactDatabaseIdentityCannotBeDuplicatedUnderTheExistingSchema()
    {
        var store = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var history = await AcquisitionHistoryWorkload.SeedAsync(store, 1, 129);
        var batch = history[0];
        var before = await SnapshotAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var duplicate = connection.CreateCommand();
        BindStream(duplicate, batch.Checkpoint.StreamId);
        duplicate.CommandText = """
            INSERT dbo.MachineObservation
              (MachineId,StreamKeyBinary,InstanceId,Sequence,Position,Source,Address,SignalType,ObservationValue,Quality,ObservedAt)
            SELECT MachineId,StreamKeyBinary,InstanceId,Sequence,Position+10000,Source,Address,SignalType,ObservationValue,Quality,ObservedAt
            FROM dbo.MachineObservation WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Position=2;
            """;
        var error = await Assert.ThrowsAsync<SqlException>(() => duplicate.ExecuteNonQueryAsync());
        Assert.Equal(2627, error.Number);
        Assert.Contains("PK_MachineObservation", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await store.ReadExactAsync(batch.Checkpoint.StreamId, Identity(batch))).Status);
    }

    [Fact]
    public async Task ExactMachineStreamAndInstanceIdentitySurvivesCollidingSequences()
    {
        var store = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var stream = new ObservationStreamId(MachineId.New(), "case-sensitive-stream");
        var first = AcquisitionHistoryWorkload.Batch(stream, null, 129);
        await store.CommitAsync(first);
        foreach (var other in new[] { new ObservationStreamId(stream.MachineId, "CASE-SENSITIVE-STREAM"),
            new ObservationStreamId(MachineId.New(), stream.StreamKey) })
        {
            var collision = AcquisitionHistoryWorkload.Batch(other, null, 129);
            await store.CommitAsync(new ObservationIngestionBatch(collision.ExpectedCheckpoint, collision.Checkpoint,
                collision.Observations.Select(item => new SequencedMachineObservation(item.Sequence, item.Observation with { Value = "OTHER" })).ToArray(),
                collision.SuccessfulContactTime, collision.AcquisitionEvidence));
        }
        var reset = AcquisitionHistoryWorkload.Batch(stream, null, 129);
        var second = new ObservationIngestionBatch(first.Checkpoint, new(stream, 2, reset.Checkpoint.NextSequence),
            reset.Observations.Select(item => new SequencedMachineObservation(item.Sequence, item.Observation with { Value = "NEW-INSTANCE" })).ToArray(),
            first.SuccessfulContactTime.AddSeconds(1), new(Guid.NewGuid(), 1, 1, reset.Checkpoint.NextSequence,
                [new(AcquisitionRecoveryReason.InstanceChanged, 1, 2, first.Checkpoint.NextSequence, 1, "test-reset", "v1")]));
        await store.CommitAsync(second);
        var restarted = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await restarted.ReadExactAsync(stream, Identity(first))).Status);
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await restarted.ReadExactAsync(stream, Identity(second))).Status);
        await restarted.CommitAsync(first); // Historical replay must not rewind the current instance.
        Assert.Equal(second.Checkpoint, await restarted.ReadCheckpointAsync(stream));
    }

    [Fact]
    public async Task DuplicateOccurrencesAndReplayedPositionsAreComparedWithoutAllocatingAgain()
    {
        var store = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var stream = new ObservationStreamId(MachineId.New(), "overlapping-batch");
        var first = AcquisitionHistoryWorkload.Batch(stream, null, 129);
        await store.CommitAsync(first);
        var fresh = AcquisitionHistoryWorkload.Batch(stream, first.Checkpoint, 1);
        var mixed = new ObservationIngestionBatch(first.Checkpoint, fresh.Checkpoint,
            [.. first.Observations, first.Observations[128], fresh.Observations[0]], fresh.SuccessfulContactTime,
            new(Guid.NewGuid(), 1, 1, fresh.Checkpoint.NextSequence,
                [new(AcquisitionRecoveryReason.OutOfRange, 1, 1, first.Checkpoint.NextSequence, 1, "test-overlap", "v1")]));
        await store.CommitAsync(mixed);
        using var capture = new AcquisitionPersistenceCapture(fixture.ConnectionString);
        var restarted = new SqlServerObservationIngestionStore(capture.ConnectionString);
        var result = await restarted.ReadExactAsync(stream, Identity(mixed));
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, result.Status);
        Assert.Equal(129UL, result.Record!.AcceptedBefore!.Value);
        Assert.Equal(130UL, result.Record.AcceptedThrough!.Value);
        Assert.Equal(7L, capture.SourceCommands);
        var before = await SnapshotAsync();
        await restarted.CommitAsync(mixed);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task CancellationDuringPredecessorVerificationRollsBackAllFiveAcquisitionStructures()
    {
        var store = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var history = await AcquisitionHistoryWorkload.SeedAsync(store, 2, 129);
        var latest = history[^1];
        var next = AcquisitionHistoryWorkload.Batch(latest.Checkpoint.StreamId, latest.Checkpoint, 129);
        var before = await SnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        using var capture = new AcquisitionPersistenceCapture(fixture.ConnectionString, activity =>
        {
            if (activity.OperationName == "acquisition.evidence.prefix.read-validate" &&
                Convert.ToInt32(activity.GetTagItem("publication.count"), CultureInfo.InvariantCulture) > 0) cancellation.Cancel();
        });
        var measured = new SqlServerObservationIngestionStore(capture.ConnectionString);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measured.CommitAsync(next, cancellation.Token).AsTask());
        Assert.Contains(capture.Spans, activity => activity.OperationName == "acquisition.raw-state.write" && Equals(activity.GetTagItem("stage.completed"), true));
        Assert.Contains(capture.Spans, activity => activity.OperationName == "acquisition.transaction.rollback" && Equals(activity.GetTagItem("stage.completed"), true));
        Assert.Equal(before, await SnapshotAsync());
        await store.CommitAsync(next); // A fresh attempt can publish after rollback.
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, (await store.ReadExactAsync(next.Checkpoint.StreamId, Identity(next))).Status);
    }

    [Fact]
    public async Task CompetingLongHistoryWritersPublishExactlyOneSuccessor()
    {
        var store = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        var history = await AcquisitionHistoryWorkload.SeedAsync(store, 2, 129);
        var current = history[^1];
        var a = AcquisitionHistoryWorkload.Batch(current.Checkpoint.StreamId, current.Checkpoint, 129);
        var b = AcquisitionHistoryWorkload.Batch(current.Checkpoint.StreamId, current.Checkpoint, 130);
        async Task<bool> Attempt(ObservationIngestionBatch batch)
        {
            try { await new SqlServerObservationIngestionStore(fixture.ConnectionString).CommitAsync(batch); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Attempt(a), Attempt(b));
        Assert.Single(outcomes, success => success);
        var winner = outcomes[0] ? a : b;
        var restarted = new SqlServerObservationIngestionStore(fixture.ConnectionString);
        Assert.Equal(winner.Checkpoint, await restarted.ReadCheckpointAsync(winner.Checkpoint.StreamId));
        var read = await restarted.ReadExactAsync(winner.Checkpoint.StreamId, Identity(winner));
        Assert.Equal(AcquisitionEvidenceReadStatus.Found, read.Status);
        Assert.Equal(3, read.Record!.PublicationOrdinal);
    }

    private static string Identity(ObservationIngestionBatch batch) =>
        Convert.ToHexString(AcquisitionEvidenceCodec.Identity(batch, AcquisitionEvidenceCodec.Encode(batch)));

    private static void BindStream(SqlCommand command, ObservationStreamId stream)
    {
        command.Parameters.AddWithValue("@Machine", stream.MachineId.Value);
        command.Parameters.Add("@Stream", System.Data.SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(stream.StreamKey);
    }

    private async Task<string> SnapshotAsync()
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT * FROM dbo.ObservationStreamCheckpoint ORDER BY MachineId,StreamKeyBinary FOR JSON PATH,INCLUDE_NULL_VALUES) AS Checkpoints,
              (SELECT * FROM dbo.MachineObservation ORDER BY MachineId,StreamKeyBinary,Position FOR JSON PATH,INCLUDE_NULL_VALUES) AS Observations,
              (SELECT * FROM dbo.AcquisitionContactAuthority ORDER BY MachineId,StreamKeyBinary FOR JSON PATH,INCLUDE_NULL_VALUES) AS ContactAuthority,
              (SELECT * FROM dbo.AcquisitionEvidencePublication ORDER BY MachineId,StreamKeyBinary,PublicationOrdinal FOR JSON PATH,INCLUDE_NULL_VALUES) AS Publications,
              (SELECT * FROM dbo.AcquisitionEvidenceHead ORDER BY MachineId,StreamKeyBinary FOR JSON PATH,INCLUDE_NULL_VALUES) AS Heads
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
