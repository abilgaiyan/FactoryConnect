using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionEvidenceIntegrationTests
{
    [Fact]
    public async Task HistoricalReplayAfterNewerContactAndRestartReturnsOriginalIdentityWithoutMutation()
    {
        await using var f=await Fixture.CreateAsync(); var first=AcquisitionEvidenceCodecTests.Batch(); await f.Store.CommitAsync(first);
        var original=await f.Read(first); Assert.Equal(AcquisitionEvidenceReadStatus.Found,original.Status);
        var next=new ObservationIngestionBatch(first.Checkpoint,first.Checkpoint,[],first.SuccessfulContactTime.AddSeconds(1),
            new(Guid.NewGuid(),first.Checkpoint.NextSequence,1,10,[]));
        await f.Store.CommitAsync(next); await f.Store.CommitAsync(first);
        var replay=await new SqlServerAcquisitionEvidenceReader(f.Database.ConnectionString).ReadExactAsync(first.Checkpoint.StreamId,original.Record!.Identity);
        Assert.Equal(original.Record.Identity,replay.Record!.Identity); Assert.Equal(1,replay.Record.PublicationOrdinal);
        Assert.Equal(2,(await f.Read(next)).Record!.PublicationOrdinal);
        Assert.Equal(next.SuccessfulContactTime,(await f.Store.ReadAcquisitionContactAuthorityAsync(first.Checkpoint.StreamId))!.SuccessfulContactTime);
        Assert.Equal(2L,await f.Count());
    }
    [Fact]
    public async Task SamePublicationChangedTimestampCursorOrPayloadFailsWithoutMutation()
    {
        await using var f=await Fixture.CreateAsync(); var first=AcquisitionEvidenceCodecTests.Batch(); await f.Store.CommitAsync(first);
        var changed=new ObservationIngestionBatch(first.ExpectedCheckpoint,first.Checkpoint,first.Observations,first.SuccessfulContactTime.AddSeconds(1),first.AcquisitionEvidence);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.CommitAsync(changed).AsTask());
        var cursor=new ObservationIngestionBatch(null,new(first.Checkpoint.StreamId,1,11),first.Observations,first.SuccessfulContactTime,
            first.AcquisitionEvidence! with { LastSequence=11 });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.CommitAsync(cursor).AsTask());
        var payload=new ObservationIngestionBatch(null,first.Checkpoint,[],first.SuccessfulContactTime,first.AcquisitionEvidence);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.CommitAsync(payload).AsTask());
        var identity=new ObservationIngestionBatch(null,first.Checkpoint,first.Observations,first.SuccessfulContactTime,
            first.AcquisitionEvidence! with { PublicationId=Guid.NewGuid() });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Store.CommitAsync(identity).AsTask());
        Assert.Equal(1L,await f.Count()); Assert.Equal(first.Checkpoint,await f.Store.ReadCheckpointAsync(first.Checkpoint.StreamId));
    }
    [Theory]
    [InlineData("ObservationStreamCheckpoint")]
    [InlineData("MachineObservation")]
    [InlineData("AcquisitionContactAuthority")]
    [InlineData("AcquisitionEvidencePublication")]
    [InlineData("AcquisitionEvidenceHead")]
    public async Task FailureAtEachInsertStageRollsBackObservationsCheckpointAndContact(string table)
    {
        await using var f=await Fixture.CreateAsync();
        await f.Execute($"CREATE TRIGGER dbo.FailEvidence ON dbo.{table} AFTER INSERT AS THROW 51000, 'injected failure', 1;");
        var batch=AcquisitionEvidenceCodecTests.Batch(); await Assert.ThrowsAsync<SqlException>(()=>f.Store.CommitAsync(batch).AsTask());
        Assert.Null(await f.Store.ReadCheckpointAsync(batch.Checkpoint.StreamId));
        Assert.Null(await f.Store.ReadAcquisitionContactAuthorityAsync(batch.Checkpoint.StreamId));
        Assert.Equal(0L,await f.Count());
        Assert.Empty((await f.Store.ReadAsync(new(batch.Checkpoint.StreamId,null,10))).Observations);
    }
    [Fact]
    public async Task RecoveryEventsRemainOrderedInsideClosedPublicationAndCannotBeOmitted()
    {
        await using var f=await Fixture.CreateAsync(); var first=AcquisitionEvidenceCodecTests.Batch(); await f.Store.CommitAsync(first);
        var losses=new[] {
            new AcquisitionRecoveryEvidence(AcquisitionRecoveryReason.OutOfRange,1,1,10,20,"out-of-range-header","v1"),
            new AcquisitionRecoveryEvidence(AcquisitionRecoveryReason.InstanceChanged,1,2,20,30,"instance-header","v1") };
        var recovered=AcquisitionEvidenceCodecTests.Batch(first.Checkpoint.StreamId,expected:first.Checkpoint,instance:2,requested:30,next:40,recoveries:[..losses]);
        await f.Store.CommitAsync(recovered); var record=(await f.Read(recovered)).Record!;
        Assert.Equal(2,record.RecoveryIdentities.Length); Assert.NotEqual(record.RecoveryIdentities[0],record.RecoveryIdentities[1]);
        Assert.Equal((await f.Read(first)).Record!.Identity,record.PreviousIdentity);
        Assert.Equal(losses,record.Batch.AcquisitionEvidence!.Recoveries);
        var eventRead=await f.Store.ReadRecoveryExactAsync(first.Checkpoint.StreamId,record.Identity,record.RecoveryIdentities[1]);
        Assert.Equal(losses[1],eventRead.Evidence); Assert.Equal(1,eventRead.EventOrdinal);
        var omitted=new ObservationIngestionBatch(recovered.ExpectedCheckpoint,recovered.Checkpoint,recovered.Observations,recovered.SuccessfulContactTime,
            recovered.AcquisitionEvidence! with { Recoveries=[] });
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Store.CommitAsync(omitted).AsTask());
        await Assert.ThrowsAsync<SqlException>(()=>f.Execute("DELETE dbo.AcquisitionEvidencePublication WHERE PublicationOrdinal=1;"));
        await f.Execute("ALTER TABLE dbo.AcquisitionEvidencePublication NOCHECK CONSTRAINT FK_AcquisitionEvidencePublication_Previous; DELETE dbo.AcquisitionEvidencePublication WHERE PublicationOrdinal=1;");
        Assert.Equal(AcquisitionEvidenceReadStatus.Corrupt,(await f.Read(recovered)).Status);
    }
    [Fact]
    public async Task ConcurrentIdenticalReplayIsIdempotentAndCompetingPublicationLosesCas()
    {
        await using var f=await Fixture.CreateAsync(); var batch=AcquisitionEvidenceCodecTests.Batch();
        await Task.WhenAll(f.Store.CommitAsync(batch).AsTask(),f.Store.CommitAsync(batch).AsTask()); Assert.Equal(1L,await f.Count());
        var a=AcquisitionEvidenceCodecTests.Batch(batch.Checkpoint.StreamId,expected:batch.Checkpoint,requested:10,next:20);
        var b=AcquisitionEvidenceCodecTests.Batch(batch.Checkpoint.StreamId,expected:batch.Checkpoint,requested:10,next:21);
        async Task<bool> Attempt(ObservationIngestionBatch item) { try { await f.Store.CommitAsync(item); return true; } catch(InvalidOperationException) { return false; } }
        var results=await Task.WhenAll(Attempt(a),Attempt(b)); Assert.Single(results,x=>x); Assert.Equal(2L,await f.Count());
    }
    [Theory]
    [InlineData("UPDATE dbo.AcquisitionEvidencePublication SET ContentHash=0x01;",AcquisitionEvidenceReadStatus.Corrupt)]
    [InlineData("UPDATE dbo.AcquisitionEvidencePublication SET PayloadVersion=2;",AcquisitionEvidenceReadStatus.UnsupportedEncoding)]
    public async Task CorruptionAndUnsupportedEncodingHaveExplicitResults(string sql,AcquisitionEvidenceReadStatus expected)
    {
        await using var f=await Fixture.CreateAsync(); var batch=AcquisitionEvidenceCodecTests.Batch(); await f.Store.CommitAsync(batch);
        await f.Execute(sql); Assert.Equal(expected,(await f.Read(batch)).Status);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public SqlStartupIsolatedDatabase Database { get; }
        public SqlServerObservationIngestionStore Store { get; }
        private Fixture(SqlStartupIsolatedDatabase db) { Database=db; Store=new(db.ConnectionString); }
        public static async Task<Fixture> CreateAsync()
        {
            var db=await SqlStartupIsolatedDatabase.CreateAsync();
            try { await using var c=new SqlConnection(db.ConnectionString); await c.OpenAsync();
                await new SqlServerMigrationEngine(SqlMigrationCatalog.Load(),new Clock()).ApplyAsync(c,TimeSpan.FromMinutes(2),CancellationToken.None); return new(db); }
            catch { await db.DisposeAsync(); throw; }
        }
        public ValueTask<AcquisitionEvidenceReadResult> Read(ObservationIngestionBatch b) => Store.ReadExactAsync(b.Checkpoint.StreamId,
            Convert.ToHexString(AcquisitionEvidenceCodec.Identity(b,AcquisitionEvidenceCodec.Encode(b))));
        public async Task Execute(string sql) { await using var c=new SqlConnection(Database.ConnectionString); await c.OpenAsync(); await using var cmd=new SqlCommand(sql,c); await cmd.ExecuteNonQueryAsync(); }
        public async Task<long> Count() { await using var c=new SqlConnection(Database.ConnectionString); await c.OpenAsync(); await using var cmd=new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.AcquisitionEvidencePublication;",c); return (long)(await cmd.ExecuteScalarAsync())!; }
        public ValueTask DisposeAsync()=>Database.DisposeAsync();
        private sealed class Clock : ISqlMigrationUtcClock { public DateTimeOffset UtcNow=>DateTimeOffset.UnixEpoch; }
    }
}
