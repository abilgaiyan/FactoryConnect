using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerProductionContextActivityReaderIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerProductionContextActivityReaderIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadsAscendingHistoryAndAppliesStrictAfterPosition()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        await PublishAsync(store, id, 1, MachineState.Running, 101, null, null);
        await PublishAsync(store, id, 2, MachineState.Idle, 102, new(1), new(0));
        await PublishAsync(store, id, 3, MachineState.Fault, 103, new(2), new(1));
        var reader = CreateReader(id.ProcessorId);

        var all = await reader.ReadAsync(id.StreamId, null, 10, CancellationToken.None);
        var after = await reader.ReadAsync(id.StreamId, new ObservationPosition(1), 10, CancellationToken.None);

        Assert.Equal([1UL, 2UL, 3UL], all.Select(item => item.Position.Value));
        Assert.Equal([2UL, 3UL], after.Select(item => item.Position.Value));
        Assert.Equal([101UL, 102UL, 103UL], all.Select(item => item.InstanceId));
        Assert.Equal([1UL, 2UL, 3UL], all.Select(item => item.Sequence));
    }

    [Fact]
    public async Task BatchSizeTruncatesAtRequestedBoundary()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        await PublishAsync(store, id, 1, MachineState.Running, 111, null, null);
        await PublishAsync(store, id, 2, MachineState.Idle, 112, new(1), new(0));
        var reader = CreateReader(id.ProcessorId);

        var first = await reader.ReadAsync(id.StreamId, null, 1, CancellationToken.None);
        var boundary = await reader.ReadAsync(id.StreamId, null, 2, CancellationToken.None);

        Assert.Single(first);
        Assert.Equal(1UL, first[0].Position.Value);
        Assert.Equal(2, boundary.Count);
    }

    [Fact]
    public async Task IdenticalPositionsUseRevisionAndOutputOrdinalTieBreakers()
    {
        var id = await CreateIdentityAsync();
        await InsertAuthorityAsync(id);
        await InsertPeriodAsync(id, 5, 2, 1, 201, 21, MachineState.Fault);
        await InsertPeriodAsync(id, 5, 1, 1, 202, 12, MachineState.Idle);
        await InsertPeriodAsync(id, 5, 1, 0, 203, 11, MachineState.Running);

        var result = await CreateReader(id.ProcessorId)
            .ReadAsync(id.StreamId, null, 10, CancellationToken.None);

        Assert.Equal([203UL, 202UL, 201UL], result.Select(item => item.InstanceId));
    }

    [Fact]
    public async Task EmptyLineageReturnsEmptyList()
    {
        var id = await CreateIdentityAsync();

        var result = await CreateReader(id.ProcessorId)
            .ReadAsync(id.StreamId, null, 10, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CorruptIntervalThrowsInvalidOperationException()
    {
        var id = await CreateIdentityAsync();
        await InsertAuthorityAsync(id);
        await InsertPeriodAsync(id, 5, 0, 0, 301, 1, MachineState.Running, corruptInterval: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateReader(id.ProcessorId).ReadAsync(
                id.StreamId, null, 10, CancellationToken.None));

        Assert.Contains("interval", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreCancelledReadPropagatesCancellation()
    {
        var id = await CreateIdentityAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateReader(id.ProcessorId).ReadAsync(
                id.StreamId, null, 10, cancellation.Token));
    }

    [Fact]
    public async Task CursorReaderProjectsAuthorityPositionWithoutIndependentCursorState()
    {
        var id = await CreateIdentityAsync();
        var store = new SqlServerMachineStateActivityAuthorityStore(_fixture.ConnectionString);
        await PublishAsync(store, id, 7, MachineState.Running, 401, null, null);
        IMachineStateActivityCursorReader cursor = new JointMachineStateActivityCursorReader(store);

        var position = await cursor.ReadAsync(id.ProcessorId, id.StreamId);

        Assert.Equal(new ObservationPosition(7), position);
    }

    private SqlServerProductionContextActivityReader CreateReader(ObservationProcessorId processorId) =>
        new(_fixture.ConnectionString, processorId);

    private static async Task PublishAsync(
        SqlServerMachineStateActivityAuthorityStore store,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ulong position,
        MachineState state,
        ulong instance,
        ObservationPosition? expectedPosition,
        StateProjectionAuthorityRevision? expectedRevision)
    {
        var durablePosition = new ObservationPosition(position);
        var result = await store.PublishAsync(new MachineStateActivityAuthorityPublication(
            expectedPosition,
            expectedRevision,
            new MachineStateActivityProjection(
                id.ProcessorId,
                id.StreamId,
                durablePosition,
                [],
                state,
                state,
                Stamp),
            [],
            [
                new DurableMachineActivityPeriod(
                    id.ProcessorId,
                    durablePosition,
                    id.StreamId,
                    instance,
                    position,
                    new MachineActivityPeriod(
                        id.StreamId.MachineId,
                        state,
                        Stamp.AddMinutes(-1),
                        Stamp))
            ],
            new EvaluationAuthorityReplayIdentity(
                id.ProcessorId,
                id.StreamId,
                durablePosition,
                state,
                instance,
                new CurrentStatePolicyReference("continuity/default", "1.0"))));

        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(result);
    }

    private async Task<(ObservationProcessorId ProcessorId, ObservationStreamId StreamId)> CreateIdentityAsync()
    {
        var id = (
            new ObservationProcessorId($"state-{Guid.NewGuid():N}"),
            new ObservationStreamId(MachineId.New(), $"MTConnect:{Guid.NewGuid():N}"));

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.ObservationStreamCheckpoint
                (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
            VALUES (@MachineId, @StreamKeyBinary, @StreamKey, 1, 1);
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            id.Item2.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(id.Item2.StreamKey);
        command.Parameters.Add("@StreamKey", SqlDbType.NVarChar, 256).Value =
            id.Item2.StreamKey;
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task InsertAuthorityAsync(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.MachineStateActivityAuthority
                (MachineId, StreamKeyBinary, StateProcessorId, StateProcessorIdOrderKey,
                 Position, MachineState, ActiveState, ActiveStartedAt,
                 LastConsumedInstanceId, ContinuityPolicyIdentity,
                 ContinuityPolicyVersion, ProjectionRevision)
            VALUES
                (@MachineId, @StreamKeyBinary, @ProcessorId, @ProcessorKey,
                 5, 1, NULL, NULL, 0, N'continuity/default', N'1.0', 2);
            """;
        AddIdentityParameters(command, id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertPeriodAsync(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ulong position,
        ulong revision,
        int ordinal,
        ulong instanceId,
        ulong sequence,
        MachineState state,
        bool corruptInterval = false)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = corruptInterval
            ? """
              ALTER TABLE dbo.MachineActivityPeriodHistory
                  NOCHECK CONSTRAINT CK_MachineActivityPeriodHistory_Interval;
              INSERT dbo.MachineActivityPeriodHistory
                  (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision,
                   OutputOrdinal, Position, MachineState, StartedAt, EndedAt, InstanceId, Sequence)
              VALUES
                  (@MachineId, @StreamKeyBinary, @ProcessorKey, @Revision,
                   @Ordinal, @Position, @State, @StartedAt, @EndedAt, @InstanceId, @Sequence);
              ALTER TABLE dbo.MachineActivityPeriodHistory
                  WITH NOCHECK CHECK CONSTRAINT CK_MachineActivityPeriodHistory_Interval;
              """
            : """
              INSERT dbo.MachineActivityPeriodHistory
                  (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision,
                   OutputOrdinal, Position, MachineState, StartedAt, EndedAt, InstanceId, Sequence)
              VALUES
                  (@MachineId, @StreamKeyBinary, @ProcessorKey, @Revision,
                   @Ordinal, @Position, @State, @StartedAt, @EndedAt, @InstanceId, @Sequence);
              """;
        AddIdentityParameters(command, id);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", revision));
        command.Parameters.Add("@Ordinal", SqlDbType.Int).Value = ordinal;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position));
        command.Parameters.Add("@State", SqlDbType.TinyInt).Value = (byte)state;
        command.Parameters.Add("@StartedAt", SqlDbType.DateTimeOffset).Value =
            corruptInterval ? Stamp : Stamp.AddMinutes(-1);
        command.Parameters.Add("@EndedAt", SqlDbType.DateTimeOffset).Value =
            corruptInterval ? Stamp.AddMinutes(-1) : Stamp;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@InstanceId", instanceId));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Sequence", sequence));
        await command.ExecuteNonQueryAsync();
    }

    private static void AddIdentityParameters(
        SqlCommand command,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id)
    {
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            id.StreamId.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(id.StreamId.StreamKey);
        command.Parameters.Add("@ProcessorId", SqlDbType.NVarChar, 256).Value =
            id.ProcessorId.Value;
        command.Parameters.Add("@ProcessorKey", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(id.ProcessorId.Value);
    }

    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
}
