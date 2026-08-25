using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerReplayConcurrencyIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string DelayTriggerName =
        "TR_FC023_TestCheckpointDelay";

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerReplayConcurrencyIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IdempotentReplaySucceedsWithoutCreatingObservation()
    {
        var streamId = CreateStreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 2);
        var observation = CreateObservation(
            streamId.MachineId,
            1,
            "load",
            42.5m);
        var batch = new ObservationIngestionBatch(
            null,
            checkpoint,
            [observation]);
        var store = CreateStore();

        await store.CommitAsync(batch);
        await store.CommitAsync(batch);

        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task IdempotentReplayCannotAddObservation()
    {
        var streamId = CreateStreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 3);
        var store = CreateStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                [CreateObservation(
                    streamId.MachineId,
                    1,
                    "load",
                    10m)]));

        var replay = new ObservationIngestionBatch(
            null,
            checkpoint,
            [
                CreateObservation(
                    streamId.MachineId,
                    1,
                    "load",
                    10.00m),
                CreateObservation(
                    streamId.MachineId,
                    2,
                    "execution",
                    20m),
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(replay).AsTask());

        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task ConflictingExistingObservationIsRejectedAtomically()
    {
        var streamId = CreateStreamId();
        var initial = new ObservationCheckpoint(streamId, 42, 2);
        var next = new ObservationCheckpoint(streamId, 42, 3);
        var store = CreateStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                initial,
                [CreateObservation(
                    streamId.MachineId,
                    1,
                    "load",
                    10m)]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                new ObservationIngestionBatch(
                    initial,
                    next,
                    [CreateObservation(
                        streamId.MachineId,
                        1,
                        "load",
                        99m)])).AsTask());

        Assert.Equal(initial, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task IdenticalDuplicateWithinBatchPersistsOnce()
    {
        var streamId = CreateStreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 2);
        var observation = CreateObservation(
            streamId.MachineId,
            1,
            "load",
            10m);
        var store = CreateStore();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                [observation, observation]));

        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, await CountObservationsAsync(streamId));
    }

    [Fact]
    public async Task ConcurrentFirstCreationHasOneWinnerAndOneStaleWriter()
    {
        var streamId = CreateStreamId();
        var store = CreateStore();
        var first = new ObservationIngestionBatch(
            null,
            new ObservationCheckpoint(streamId, 42, 2),
            [CreateObservation(streamId.MachineId, 1, "first", 1m)]);
        var second = new ObservationIngestionBatch(
            null,
            new ObservationCheckpoint(streamId, 42, 3),
            [CreateObservation(streamId.MachineId, 2, "second", 2m)]);

        await AddDelayTriggerAsync();

        try
        {
            var results = await RunCompetingCommitsAsync(
                store,
                first,
                second);

            Assert.Equal(1, results.Count(result => result is null));
            Assert.Equal(
                1,
                results.Count(result =>
                    result is InvalidOperationException));
            Assert.DoesNotContain(
                results,
                result => result is not null &&
                          result is not InvalidOperationException);
        }
        finally
        {
            await DropDelayTriggerAsync();
        }
    }

    [Fact]
    public async Task ConcurrentContinuationHasOneWinnerAndOneStaleWriter()
    {
        var streamId = CreateStreamId();
        var store = CreateStore();
        var initial = new ObservationCheckpoint(streamId, 42, 2);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                initial,
                [CreateObservation(streamId.MachineId, 1, "initial", 1m)]));

        var first = new ObservationIngestionBatch(
            initial,
            new ObservationCheckpoint(streamId, 42, 3),
            [CreateObservation(streamId.MachineId, 2, "first", 2m)]);
        var second = new ObservationIngestionBatch(
            initial,
            new ObservationCheckpoint(streamId, 42, 4),
            [CreateObservation(streamId.MachineId, 3, "second", 3m)]);

        await AddDelayTriggerAsync();

        try
        {
            var results = await RunCompetingCommitsAsync(
                store,
                first,
                second);

            Assert.Equal(1, results.Count(result => result is null));
            Assert.Equal(
                1,
                results.Count(result =>
                    result is InvalidOperationException));
            Assert.DoesNotContain(
                results,
                result => result is not null &&
                          result is not InvalidOperationException);
        }
        finally
        {
            await DropDelayTriggerAsync();
        }
    }

    private SqlServerObservationIngestionStore CreateStore() =>
        new(_fixture.ConnectionString);

    private static ObservationStreamId CreateStreamId() =>
        new(MachineId.New(), $"mtconnect:{Guid.NewGuid():N}");

    private static SequencedMachineObservation CreateObservation(
        MachineId machineId,
        ulong sequence,
        string address,
        decimal value) =>
        new(
            sequence,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "mtconnect",
                Address = address,
                Type = SignalType.Numeric,
                Value = value,
                Quality = ObservationQuality.Good,
                Timestamp = DateTimeOffset.UnixEpoch,
            });

    private static async Task<Exception?[]> RunCompetingCommitsAsync(
        SqlServerObservationIngestionStore store,
        ObservationIngestionBatch first,
        ObservationIngestionBatch second)
    {
        using var start = new ManualResetEventSlim(false);

        var firstTask = Task.Run(async () =>
        {
            start.Wait();
            return await CaptureExceptionAsync(
                () => store.CommitAsync(first).AsTask());
        });
        var secondTask = Task.Run(async () =>
        {
            start.Wait();
            return await CaptureExceptionAsync(
                () => store.CommitAsync(second).AsTask());
        });

        start.Set();

        return await Task.WhenAll(firstTask, secondTask);
    }

    private static async Task<Exception?> CaptureExceptionAsync(
        Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

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

    private async Task AddDelayTriggerAsync()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TRIGGER dbo.{DelayTriggerName}
            ON dbo.ObservationStreamCheckpoint
            AFTER INSERT, UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                WAITFOR DELAY '00:00:01';
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropDelayTriggerAsync()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP TRIGGER IF EXISTS dbo.{DelayTriggerName};
            """;
        await command.ExecuteNonQueryAsync();
    }
}
