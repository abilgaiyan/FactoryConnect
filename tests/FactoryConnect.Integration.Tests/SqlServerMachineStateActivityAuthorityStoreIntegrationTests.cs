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
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMachineStateActivityAuthorityStoreIntegrationTests(SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task NewPublicationRoundTripsAndRetryIsExactReplay()
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
        Assert.Equal(first.Snapshot, read);
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

        Assert.Equal(before, await store.ReadAsync(id.ProcessorId, id.StreamId));
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
}
