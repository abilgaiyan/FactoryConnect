using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMachineStateActivityAuthorityStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string AuthorityIndexName = "PK_MachineStateActivityAuthority";
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMachineStateActivityAuthorityStoreIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task C03LostAcknowledgementRetryIsExactReplayWithoutDuplicateHistory()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        var publication = Publication(id, null, null, 1, MachineState.Running, 11);

        var first = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.NewPublication, first.Disposition);
        Assert.Equal(0UL, first.Snapshot.EvaluationAuthority.ProjectionRevision.Value);

        var retry = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, retry.Disposition);
        Assert.Equal(0UL, retry.Snapshot.EvaluationAuthority.ProjectionRevision.Value);

        var read = await store.ReadAsync(id.ProcessorId, id.StreamId);
        AssertSnapshotEquivalent(first.Snapshot, read);
        Assert.Equal(1, await CountAsync("dbo.MachineStateChangeHistory", id));
        Assert.Equal(1, await CountAsync("dbo.MachineActivityPeriodHistory", id));
    }

    [Fact]
    public async Task ConcurrentFirstPublicationsSerializeAtAuthorityIdentity()
    {
        var id = await CreateIdentityAsync();
        var storeA = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        var storeB = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        var a = Publication(id, null, null, 1, MachineState.Running, 21);
        var b = Publication(id, null, null, 2, MachineState.Fault, 22);

        var results = await Task.WhenAll(
            storeA.PublishAsync(a).AsTask(),
            storeB.PublishAsync(b).AsTask());

        Assert.Single(results.OfType<MachineStateActivityAuthorityPublicationAccepted>());
        Assert.Single(results.OfType<MachineStateActivityAuthorityPublicationConflict>());
        var current = await storeA.ReadAsync(id.ProcessorId, id.StreamId);
        Assert.NotNull(current);
        Assert.Equal(0UL, current.EvaluationAuthority.ProjectionRevision.Value);
    }

    [Fact]
    public async Task C01MissingAuthoritySlotIsProtectedByPrimaryKeyRangeLock()
    {
        var id = await CreateIdentityAsync();
        var held = HoldAtAuthorityLock(Publication(id, null, null, 1, MachineState.Running, 101));
        Task<MachineStateActivityAuthorityPublicationResult>? contender = null;
        try
        {
            var ownerSession = await held.SessionId.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var contenderStore = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
            contender = contenderStore.PublishAsync(
                Publication(id, null, null, 2, MachineState.Fault, 102)).AsTask();

            var wait = await WaitForAuthorityKeyBlockingAsync(
                ownerSession, contender, TimeSpan.FromSeconds(10));
            Assert.Equal(ownerSession, wait.BlockingSessionId);
            Assert.StartsWith("LCK_M_", wait.WaitType, StringComparison.Ordinal);
            Assert.StartsWith("KEY:", wait.WaitResource, StringComparison.Ordinal);
            Assert.Equal(AuthorityIndexName, wait.IndexName);

            held.Release.TrySetResult(true);
            Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(await held.Writer);
            Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(
                await contender.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await ReleaseAsync(held);
            if (contender is not null) await ObserveAsync(contender);
        }
    }

    [Fact]
    public async Task C02ExistingAuthoritySerializesAndContenderReclassifies()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        await store.PublishAsync(Publication(id, null, null, 1, MachineState.Running, 111));
        var held = HoldAtAuthorityLock(
            Publication(id, new(1), new(0), 2, MachineState.Fault, 112));
        Task<MachineStateActivityAuthorityPublicationResult>? contender = null;
        try
        {
            var ownerSession = await held.SessionId.Task.WaitAsync(TimeSpan.FromSeconds(10));
            contender = store.PublishAsync(
                Publication(id, new(1), new(0), 3, MachineState.Idle, 113)).AsTask();
            var wait = await WaitForAuthorityKeyBlockingAsync(
                ownerSession, contender, TimeSpan.FromSeconds(10));
            Assert.Equal(AuthorityIndexName, wait.IndexName);

            held.Release.TrySetResult(true);
            var winner = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(await held.Writer);
            Assert.Equal(1UL, winner.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
            Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(
                await contender.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await ReleaseAsync(held);
            if (contender is not null) await ObserveAsync(contender);
        }
    }

    [Fact]
    public async Task C04CancellationWhileWaitingForAuthorityLockLeavesNoMutation()
    {
        var id = await CreateIdentityAsync();
        var held = HoldAtAuthorityLock(Publication(id, null, null, 1, MachineState.Running, 121));
        using var cancellation = new CancellationTokenSource();
        Task<MachineStateActivityAuthorityPublicationResult>? contender = null;
        try
        {
            var ownerSession = await held.SessionId.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
            contender = store.PublishAsync(
                Publication(id, null, null, 2, MachineState.Fault, 122), cancellation.Token).AsTask();
            await WaitForAuthorityKeyBlockingAsync(ownerSession, contender, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => contender.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Null(await store.ReadAsync(id.ProcessorId, id.StreamId));
            held.Release.TrySetResult(true);
            var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(await held.Writer);
            Assert.Equal(0UL, accepted.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
        }
        finally
        {
            await ReleaseAsync(held);
            if (contender is not null) await ObserveAsync(contender);
        }
    }

    [Theory]
    [InlineData((int)SqlServerMachineStateActivityPublicationStage.AuthorityMutated)]
    [InlineData((int)SqlServerMachineStateActivityPublicationStage.SignalsDeleted)]
    [InlineData((int)SqlServerMachineStateActivityPublicationStage.StateHistoryInserted)]
    [InlineData((int)SqlServerMachineStateActivityPublicationStage.ActivityHistoryInserted)]
    public async Task C05ThroughC08FailureAfterMutationStageRollsBackWholePublication(int stageValue)
    {
        var stage = (SqlServerMachineStateActivityPublicationStage)stageValue;
        var id = await CreateIdentityAsync();
        var baselineStore = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        var initial = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await baselineStore.PublishAsync(Publication(id, null, null, 1, MachineState.Running, 131)));
        var before = await baselineStore.ReadAsync(id.ProcessorId, id.StreamId);
        var stateCount = await CountAsync("dbo.MachineStateChangeHistory", id);
        var activityCount = await CountAsync("dbo.MachineActivityPeriodHistory", id);
        var faulting = new SqlServerMachineStateActivityAuthorityStore(
            _fixture.ConnectionString,
            (visited, _, _) => visited == stage
                ? Task.FromException(new InjectedSqlPublicationException(stage))
                : Task.CompletedTask);

        var error = await Assert.ThrowsAsync<InjectedSqlPublicationException>(() =>
            faulting.PublishAsync(Publication(
                id, initial.Snapshot.Projection.Position,
                initial.Snapshot.EvaluationAuthority.ProjectionRevision,
                2, MachineState.Fault, 132,
                [Signal("fault", true), Signal("running", false)])).AsTask());
        Assert.Equal(stage, error.Stage);
        AssertSnapshotEquivalent(before, await baselineStore.ReadAsync(id.ProcessorId, id.StreamId));
        Assert.Equal(stateCount, await CountAsync("dbo.MachineStateChangeHistory", id));
        Assert.Equal(activityCount, await CountAsync("dbo.MachineActivityPeriodHistory", id));
    }

    [Fact]
    public async Task C09FreshLineageFailureConsumesNoRevision()
    {
        var id = await CreateIdentityAsync();
        var publication = Publication(id, null, null, 1, MachineState.Running, 141);
        var faulting = new SqlServerMachineStateActivityAuthorityStore(
            _fixture.ConnectionString,
            (stage, _, _) => stage == SqlServerMachineStateActivityPublicationStage.ActivityHistoryInserted
                ? Task.FromException(new InjectedSqlPublicationException(stage))
                : Task.CompletedTask);
        await Assert.ThrowsAsync<InjectedSqlPublicationException>(() => faulting.PublishAsync(publication).AsTask());

        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        Assert.Null(await store.ReadAsync(id.ProcessorId, id.StreamId));
        Assert.Equal(0, await CountAsync("dbo.MachineStateChangeHistory", id));
        Assert.Equal(0, await CountAsync("dbo.MachineActivityPeriodHistory", id));
        var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication));
        Assert.Equal(0UL, accepted.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
    }

    [Fact]
    public async Task C10ExactReplayAtMaximumRevisionDoesNotAllocate()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        var publication = Publication(id, null, null, 1, MachineState.Running, 151);
        await store.PublishAsync(publication);
        await SetRevisionAsync(id, ulong.MaxValue);

        var replay = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replay.Disposition);
        Assert.Equal(ulong.MaxValue, replay.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
        Assert.Equal(1, await CountAsync("dbo.MachineStateChangeHistory", id));
        Assert.Equal(1, await CountAsync("dbo.MachineActivityPeriodHistory", id));
    }

    [Fact]
    public async Task FailureAfterAuthorityMutationRollsBackWholePublication()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        var initial = Publication(id, null, null, 1, MachineState.Running, 31);
        var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(initial));
        var before = await store.ReadAsync(id.ProcessorId, id.StreamId);

        var invalidSignal = new MachineSignalValue
        {
            Key = "bad",
            Type = SignalType.Digital,
            Value = "not-a-bool",
            Quality = ObservationQuality.Good,
            Timestamp = Stamp,
        };
        var changed = Publication(
            id,
            accepted.Snapshot.Projection.Position,
            accepted.Snapshot.EvaluationAuthority.ProjectionRevision,
            2,
            MachineState.Fault,
            32,
            [invalidSignal]);

        await Assert.ThrowsAsync<InvalidCastException>(() => store.PublishAsync(changed).AsTask());

        AssertSnapshotEquivalent(before, await store.ReadAsync(id.ProcessorId, id.StreamId));
        Assert.Equal(1, await CountAsync("dbo.MachineStateChangeHistory", id));
        Assert.Equal(1, await CountAsync("dbo.MachineActivityPeriodHistory", id));
    }

    [Fact]
    public async Task PreCancelledPublicationLeavesFreshLineageAbsent()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.PublishAsync(Publication(id, null, null, 1, MachineState.Running, 41), cts.Token).AsTask());

        Assert.Null(await store.ReadAsync(id.ProcessorId, id.StreamId));
    }

    private HeldPublication HoldAtAuthorityLock(MachineStateActivityAuthorityPublication publication)
    {
        var sessionId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SqlServerMachineStateActivityAuthorityStore(
            _fixture.ConnectionString,
            async (stage, session, cancellationToken) =>
            {
                if (stage != SqlServerMachineStateActivityPublicationStage.AuthorityLocked) return;
                sessionId.TrySetResult(session);
                await release.Task.WaitAsync(cancellationToken);
            });
        return new HeldPublication(store.PublishAsync(publication).AsTask(), sessionId, release);
    }

    private async Task<BlockingWaitSnapshot> WaitForAuthorityKeyBlockingAsync(
        int ownerSessionId,
        Task contender,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        BlockingWaitSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (contender.IsCompleted)
                throw new Xunit.Sdk.XunitException("Contender completed before an authority KEY wait was observed.");
            await using var connection = _fixture.CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (1) r.wait_type, r.wait_resource,
                    CAST(r.blocking_session_id AS int), i.name
                FROM sys.dm_exec_requests AS r
                INNER JOIN sys.dm_tran_locks AS l
                    ON l.request_session_id = r.session_id
                   AND l.request_status = N'WAIT'
                   AND l.resource_type = N'KEY'
                INNER JOIN sys.partitions AS p
                    ON p.hobt_id = l.resource_associated_entity_id
                INNER JOIN sys.indexes AS i
                    ON i.object_id = p.object_id AND i.index_id = p.index_id
                WHERE r.blocking_session_id = @OwnerSessionId
                  AND r.wait_type LIKE N'LCK_M[_]%'
                  AND r.wait_resource LIKE N'KEY:%'
                  AND i.object_id = OBJECT_ID(N'dbo.MachineStateActivityAuthority')
                ORDER BY CASE WHEN i.name = @ExpectedIndexName THEN 0 ELSE 1 END, i.index_id;
                """;
            command.Parameters.Add("@OwnerSessionId", SqlDbType.Int).Value = ownerSessionId;
            command.Parameters.Add("@ExpectedIndexName", SqlDbType.NVarChar, 128).Value = AuthorityIndexName;
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                last = new BlockingWaitSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
                if (string.Equals(last.IndexName, AuthorityIndexName, StringComparison.Ordinal)) return last;
            }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException(last is null
            ? $"No authority KEY wait blocked by session {ownerSessionId} was observed."
            : $"Authority wait used index '{last.IndexName}', not '{AuthorityIndexName}'.");
    }

    private async Task SetRevisionAsync(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ulong revision)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            foreach (var table in new[]
                     {
                         "dbo.MachineStateActivityAuthority",
                         "dbo.MachineStateChangeHistory",
                         "dbo.MachineActivityPeriodHistory",
                     })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"UPDATE {table} SET ProjectionRevision=@Revision WHERE MachineId=@MachineId AND StreamKeyBinary=@StreamKeyBinary AND StateProcessorIdOrderKey=@ProcessorKey;";
                command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", revision));
                command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = id.StreamId.MachineId.Value;
                command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(id.StreamId.StreamKey);
                command.Parameters.Add("@ProcessorKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(id.ProcessorId.Value);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ReleaseAsync(HeldPublication held)
    {
        held.Release.TrySetResult(true);
        await ObserveAsync(held.Writer);
    }

    private static async Task ObserveAsync(Task task)
    {
        if (!task.IsCompleted)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { }
        }
    }

    private static void AssertSnapshotEquivalent(
        MachineStateActivityAuthoritySnapshot? expected,
        MachineStateActivityAuthoritySnapshot? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.Projection.ProcessorId, actual.Projection.ProcessorId);
        Assert.Equal(expected.Projection.StreamId, actual.Projection.StreamId);
        Assert.Equal(expected.Projection.Position, actual.Projection.Position);
        Assert.Equal(expected.Projection.State, actual.Projection.State);
        Assert.Equal(expected.Projection.ActiveState, actual.Projection.ActiveState);
        Assert.Equal(expected.Projection.ActiveStartedAt, actual.Projection.ActiveStartedAt);
        Assert.Equal(expected.Projection.Signals, actual.Projection.Signals);
        Assert.Equal(expected.EvaluationAuthority, actual.EvaluationAuthority);
    }

    private async Task<(ObservationProcessorId ProcessorId, ObservationStreamId StreamId)> CreateIdentityAsync()
    {
        var id = (
            new ObservationProcessorId($"state-{Guid.NewGuid():N}"),
            new ObservationStreamId(MachineId.New(), $"MTConnect:{Guid.NewGuid():N}"));

        var streamKey = OrdinalStringKeyCodec.Encode(id.Item2.StreamKey);
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.ObservationStreamCheckpoint
                (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
            VALUES (@MachineId, @StreamKeyBinary, @StreamKey, 1, 1);
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = id.Item2.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value = streamKey;
        command.Parameters.Add("@StreamKey", SqlDbType.NVarChar, 256).Value = id.Item2.StreamKey;
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static MachineStateActivityAuthorityPublication Publication(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ObservationPosition? expectedPosition,
        StateProjectionAuthorityRevision? expectedRevision,
        ulong position,
        MachineState state,
        ulong instance,
        IReadOnlyList<MachineSignalValue>? signals = null)
    {
        var durablePosition = new ObservationPosition(position);
        return new MachineStateActivityAuthorityPublication(
            expectedPosition,
            expectedRevision,
            new MachineStateActivityProjection(
                id.ProcessorId, id.StreamId, durablePosition,
                signals ?? [Signal("running", state == MachineState.Running)],
                state, state, Stamp),
            [
                new DurableMachineStateChangedEvent(
                    id.ProcessorId, durablePosition, id.StreamId, instance, position,
                    new MachineStateChangedEvent(
                        id.StreamId.MachineId,
                        state == MachineState.Running ? MachineState.Idle : MachineState.Running,
                        state,
                        Stamp))
            ],
            [
                new DurableMachineActivityPeriod(
                    id.ProcessorId, durablePosition, id.StreamId, instance, position,
                    new MachineActivityPeriod(id.StreamId.MachineId, state, Stamp.AddMinutes(-1), Stamp))
            ],
            new EvaluationAuthorityReplayIdentity(
                id.ProcessorId, id.StreamId, durablePosition, state, instance,
                new CurrentStatePolicyReference("continuity/default", "1.0")));
    }

    private static MachineSignalValue Signal(string key, bool value) => new()
    {
        Key = key,
        Type = SignalType.Digital,
        Value = value,
        Source = "test",
        Quality = ObservationQuality.Good,
        Timestamp = Stamp,
    };

    private async Task<int> CountAsync(
        string table,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE MachineId=@MachineId AND StreamKeyBinary=@StreamKeyBinary AND StateProcessorIdOrderKey=@ProcessorKey;";
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = id.StreamId.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(id.StreamId.StreamKey);
        command.Parameters.Add("@ProcessorKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(id.ProcessorId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    private sealed record HeldPublication(
        Task<MachineStateActivityAuthorityPublicationResult> Writer,
        TaskCompletionSource<int> SessionId,
        TaskCompletionSource<bool> Release);

    private sealed record BlockingWaitSnapshot(
        string WaitType,
        string WaitResource,
        int BlockingSessionId,
        string IndexName);

    private sealed class InjectedSqlPublicationException(
        SqlServerMachineStateActivityPublicationStage stage) : Exception
    {
        public SqlServerMachineStateActivityPublicationStage Stage { get; } = stage;
    }
}
