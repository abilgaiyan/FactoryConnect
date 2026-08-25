using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAtomicCommitIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerAtomicCommitIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FirstCommitPersistsCheckpointAndObservationTogether()
    {
        var streamId = CreateStreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 10, 2);
        var store = CreateStore();
        var batch = new ObservationIngestionBatch(
            null,
            checkpoint,
            [CreateObservation(streamId.MachineId, 1, 42.5m)]);

        await store.CommitAsync(batch);

        Assert.Equal(
            checkpoint,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task ContinuationRequiresExpectedCheckpointAndAdvancesAtomically()
    {
        var streamId = CreateStreamId();
        var initial = new ObservationCheckpoint(streamId, 10, 2);
        var next = new ObservationCheckpoint(streamId, 10, 3);
        var store = CreateStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                initial,
                [CreateObservation(streamId.MachineId, 1, 10m)]));

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial,
                next,
                [CreateObservation(streamId.MachineId, 2, 20m)]));

        Assert.Equal(next, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task EmptyBatchCanAdvanceCheckpoint()
    {
        var streamId = CreateStreamId();
        var initial = new ObservationCheckpoint(streamId, 10, 2);
        var next = new ObservationCheckpoint(streamId, 10, 5);
        var store = CreateStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                initial,
                [CreateObservation(streamId.MachineId, 1, 10m)]));

        await store.CommitAsync(
            new ObservationIngestionBatch(initial, next, []));

        Assert.Equal(next, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task ObservationConstraintFailureRollsBackCheckpointAndObservation()
    {
        var streamId = CreateStreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 10, 2);
        var store = CreateStore();
        var invalidObservation = CreateObservation(
            streamId.MachineId,
            1,
            42.5m,
            (ObservationQuality)99);

        await Assert.ThrowsAsync<SqlException>(
            async () => await store.CommitAsync(
                new ObservationIngestionBatch(
                    null,
                    checkpoint,
                    [invalidObservation])));

        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Equal(0, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task StaleExpectedCheckpointLeavesDurableStateUnchanged()
    {
        var streamId = CreateStreamId();
        var initial = new ObservationCheckpoint(streamId, 10, 2);
        var stale = new ObservationCheckpoint(streamId, 10, 1);
        var proposed = new ObservationCheckpoint(streamId, 10, 3);
        var store = CreateStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                initial,
                [CreateObservation(streamId.MachineId, 1, 10m)]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.CommitAsync(
                new ObservationIngestionBatch(
                    stale,
                    proposed,
                    [CreateObservation(streamId.MachineId, 2, 20m)])));

        Assert.Equal(initial, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, await CountObservationsAsync(streamId));
    }

    private SqlServerObservationIngestionStore CreateStore() =>
        new(_fixture.ConnectionString);

    private static ObservationStreamId CreateStreamId() =>
        new(MachineId.New(), $"mtconnect:{Guid.NewGuid():N}");

    private static SequencedMachineObservation CreateObservation(
        MachineId machineId,
        ulong sequence,
        decimal value,
        ObservationQuality quality = ObservationQuality.Good) =>
        new(
            sequence,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "mtconnect",
                Address = $"load-{sequence}",
                Type = SignalType.Numeric,
                Value = value,
                Quality = quality,
                Timestamp = DateTimeOffset.UtcNow,
            });

    private async Task<int> CountObservationsAsync(
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;
        command.Parameters.AddWithValue(
            "@MachineId",
            streamId.MachineId.Value);
        command.Parameters.AddWithValue(
            "@StreamKeyBinary",
            OrdinalStringKeyCodec.Encode(streamId.StreamKey));

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
