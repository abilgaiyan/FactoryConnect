using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using System.Data;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerCheckpointReadIntegrationTests(
    SqlServerTestDatabaseFixture fixture) :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task MissingCheckpointReturnsNull()
    {
        var store = CreateStore();
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "mtconnect:missing");

        var checkpoint = await store.ReadCheckpointAsync(streamId);

        Assert.Null(checkpoint);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task ExistingCheckpointReturnsExactValues()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "mtconnect:CNC-01");
        await InsertCheckpointAsync(streamId, 42UL, 101UL);

        var checkpoint = await CreateStore().ReadCheckpointAsync(streamId);

        Assert.NotNull(checkpoint);
        Assert.Equal(streamId, checkpoint.StreamId);
        Assert.Equal(42UL, checkpoint.InstanceId);
        Assert.Equal(101UL, checkpoint.NextSequence);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task StreamIdentityIsCaseSensitive()
    {
        var machineId = MachineId.New();
        var storedStream = new ObservationStreamId(
            machineId,
            "MTConnect:CNC-01");
        var differentStream = new ObservationStreamId(
            machineId,
            "mtconnect:CNC-01");
        await InsertCheckpointAsync(storedStream, 1UL, 2UL);

        var checkpoint = await CreateStore().ReadCheckpointAsync(
            differentStream);

        Assert.Null(checkpoint);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task StreamIdentityPreservesTrailingSpaces()
    {
        var machineId = MachineId.New();
        var storedStream = new ObservationStreamId(
            machineId,
            "mtconnect:CNC-01 ");
        var differentStream = new ObservationStreamId(
            machineId,
            "mtconnect:CNC-01");
        await InsertCheckpointAsync(storedStream, 1UL, 2UL);

        var checkpoint = await CreateStore().ReadCheckpointAsync(
            differentStream);

        Assert.Null(checkpoint);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task MachineIdentityIsIsolated()
    {
        const string streamKey = "mtconnect:CNC-01";
        var storedStream = new ObservationStreamId(
            MachineId.New(),
            streamKey);
        var differentMachineStream = new ObservationStreamId(
            MachineId.New(),
            streamKey);
        await InsertCheckpointAsync(storedStream, 1UL, 2UL);

        var checkpoint = await CreateStore().ReadCheckpointAsync(
            differentMachineStream);

        Assert.Null(checkpoint);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task MaximumUInt64ValuesRoundTrip()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "mtconnect:max");
        await InsertCheckpointAsync(
            streamId,
            ulong.MaxValue,
            ulong.MaxValue);

        var checkpoint = await CreateStore().ReadCheckpointAsync(streamId);

        Assert.NotNull(checkpoint);
        Assert.Equal(ulong.MaxValue, checkpoint.InstanceId);
        Assert.Equal(ulong.MaxValue, checkpoint.NextSequence);
    }

    [Fact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task ReadCheckpointHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "mtconnect:cancelled");

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await CreateStore().ReadCheckpointAsync(
                streamId,
                cancellation.Token));
    }

    private SqlServerObservationIngestionStore CreateStore() =>
        new(fixture.ConnectionString);

    private async Task InsertCheckpointAsync(
        ObservationStreamId streamId,
        ulong instanceId,
        ulong nextSequence)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.ObservationStreamCheckpoint
                (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
            VALUES
                (@MachineId, @StreamKeyBinary, @StreamKey, @InstanceId, @NextSequence);
            """;

        command.Parameters.Add(
            new SqlParameter("@MachineId", SqlDbType.UniqueIdentifier)
            {
                Value = streamId.MachineId.Value,
            });
        command.Parameters.Add(
            new SqlParameter("@StreamKeyBinary", SqlDbType.VarBinary, 512)
            {
                Value = OrdinalStringKeyCodec.Encode(streamId.StreamKey),
            });
        command.Parameters.Add(
            new SqlParameter("@StreamKey", SqlDbType.NVarChar, 256)
            {
                Value = streamId.StreamKey,
            });
        command.Parameters.Add(
            new SqlParameter("@InstanceId", SqlDbType.Decimal)
            {
                Precision = 20,
                Scale = 0,
                Value = (decimal)instanceId,
            });
        command.Parameters.Add(
            new SqlParameter("@NextSequence", SqlDbType.Decimal)
            {
                Precision = 20,
                Scale = 0,
                Value = (decimal)nextSequence,
            });

        await command.ExecuteNonQueryAsync();
    }
}
