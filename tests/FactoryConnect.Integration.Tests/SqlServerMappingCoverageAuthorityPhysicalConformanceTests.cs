using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMappingCoverageAuthorityPhysicalConformanceTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMappingCoverageAuthorityPhysicalConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthoritySurvivesStoreRecreation()
    {
        var (processorId, streamId) = Identity();
        await PrepareStreamAsync(streamId);
        var store = CreateStore();

        await store.CommitAsync(Commit(null, processorId, streamId, 5, 3));
        var expected = await RequiredAsync(store, processorId, streamId);

        var recreated = CreateStore();

        Assert.Equal(expected, await RequiredAsync(recreated, processorId, streamId));
    }

    [Fact]
    public async Task ProcessorOrderKeyCorruptionIsRejectedByReadAndCommit()
    {
        var (processorId, streamId) = Identity();
        await PrepareStreamAsync(streamId);
        var store = CreateStore();
        await store.CommitAsync(Commit(null, processorId, streamId, 5, 3));
        var current = await RequiredAsync(store, processorId, streamId);

        await CorruptProcessorOrderKeyAsync(processorId, streamId);

        var read = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(processorId, streamId).AsTask());
        Assert.Contains("identity", read.Message, StringComparison.OrdinalIgnoreCase);

        var commit = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                Commit(current, processorId, streamId, 8, 6)).AsTask());
        Assert.Contains("identity", commit.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevisionExhaustionAllowsReplayAndRejectsChangedCoverageAtomically()
    {
        var (processorId, streamId) = Identity();
        await PrepareStreamAsync(streamId);
        var store = CreateStore();
        await store.CommitAsync(Commit(null, processorId, streamId, 5, 3));
        await ForceRevisionAsync(processorId, streamId, ulong.MaxValue);
        var current = await RequiredAsync(store, processorId, streamId);

        await store.CommitAsync(
            Commit(current, processorId, streamId, 5, 3));
        Assert.Equal(current, await RequiredAsync(store, processorId, streamId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                Commit(current, processorId, streamId, 8, 6)).AsTask());

        Assert.Equal(current, await RequiredAsync(store, processorId, streamId));
    }

    [Fact]
    public async Task MissingCheckpointRejectsInitialPublicationWithoutCreatingAuthority()
    {
        var (processorId, streamId) = Identity();
        var store = CreateStore();

        await Assert.ThrowsAsync<SqlException>(
            () => store.CommitAsync(
                Commit(null, processorId, streamId, 5, 3)).AsTask());

        Assert.Null(await store.ReadAsync(processorId, streamId));
    }

    [Fact]
    public async Task CompetingCasHasOneChangedWinnerAndOneConflict()
    {
        var (processorId, streamId) = Identity();
        await PrepareStreamAsync(streamId);
        var store = CreateStore();
        await store.CommitAsync(Commit(null, processorId, streamId, 5, 3));
        var expected = await RequiredAsync(store, processorId, streamId);

        var first = CreateStore().CommitAsync(
            Commit(expected, processorId, streamId, 8, 6)).AsTask();
        var second = CreateStore().CommitAsync(
            Commit(expected, processorId, streamId, 9, 7)).AsTask();

        var results = await Task.WhenAll(
            ObserveAsync(first),
            ObserveAsync(second));

        Assert.Equal(1, results.Count(static result => result is null));
        Assert.Equal(
            1,
            results.Count(static result => result is InvalidOperationException));

        var current = await RequiredAsync(CreateStore(), processorId, streamId);
        Assert.Equal(new MappingAuthorityRevision(1), current.MappingRevision);
        Assert.Contains(
            current.RawConsumedThrough,
            new ObservationPosition(8),
            new ObservationPosition(9));
    }

    private SqlServerMappingCoverageAuthorityStore CreateStore() =>
        new(_fixture.ConnectionString);

    private async Task PrepareStreamAsync(ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ObservationStreamCheckpoint
                (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
            VALUES
                (@MachineId, @StreamKeyBinary, @StreamKey, 0, 0);
            """;
        AddStreamParameters(command, streamId);
        command.Parameters.Add("@StreamKey", SqlDbType.NVarChar, 256).Value =
            streamId.StreamKey;
        await command.ExecuteNonQueryAsync();
    }

    private async Task CorruptProcessorOrderKeyAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.MappingCoverageAuthority
            SET MappingProcessorIdOrderKey = @CorruptOrderKey
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND MappingProcessorId = @MappingProcessorId;
            """;
        AddStreamParameters(command, streamId);
        command.Parameters.Add("@MappingProcessorId", SqlDbType.NVarChar, 256).Value =
            processorId.Value;
        command.Parameters.Add(
            "@CorruptOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(processorId.Value + "-corrupt");
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task ForceRevisionAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ulong revision)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.MappingCoverageAuthority
            SET MappingRevision = @MappingRevision
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND MappingProcessorId = @MappingProcessorId;
            """;
        AddStreamParameters(command, streamId);
        command.Parameters.Add("@MappingProcessorId", SqlDbType.NVarChar, 256).Value =
            processorId.Value;
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@MappingRevision", revision));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static MappingCoverageCommit Commit(
        MappingCoverageAuthority? expected,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ulong raw,
        ulong? mapped) =>
        new(
            expected,
            processorId,
            streamId,
            new ObservationPosition(raw),
            mapped is null ? null : new ObservationPosition(mapped.Value));

    private static async Task<MappingCoverageAuthority> RequiredAsync(
        IMappingCoverageAuthorityStore store,
        ObservationProcessorId processorId,
        ObservationStreamId streamId)
    {
        var authority = await store.ReadAsync(processorId, streamId);
        Assert.NotNull(authority);
        return authority;
    }

    private static void AddStreamParameters(
        SqlCommand command,
        ObservationStreamId streamId)
    {
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            streamId.MachineId.Value;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            OrdinalStringKeyCodec.MaxCodeUnits * 2).Value =
            OrdinalStringKeyCodec.Encode(streamId.StreamKey);
    }

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId)
        Identity()
    {
        var machineId = MachineId.New();
        return (
            new ObservationProcessorId($"mapping-{Guid.NewGuid():N}"),
            new ObservationStreamId(machineId, $"MTConnect:{Guid.NewGuid():N}"));
    }
}
