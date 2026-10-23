using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerObservationReadTimeoutTests
{
    [Fact]
    public async Task ObservationReadTimesOutUnderSqlLockThenSucceedsAfterRelease()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using (var migrationConnection = new SqlConnection(database.ConnectionString))
        {
            await migrationConnection.OpenAsync();
            await SqlServerMigrationEngine.CreateDefault().ApplyAsync(
                migrationConnection, TimeSpan.FromMinutes(2), CancellationToken.None);
        }

        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:gg003-timeout-test");
        var processor = new ObservationProcessorId("gg003-read-timeout-probe");
        var store = new SqlServerObservationIngestionStore(
            database.ConnectionString, observationReadCommandTimeoutSeconds: 1);
        var checkpoint = new ObservationCheckpoint(stream, 1, 2);
        await store.CommitAsync(new ObservationIngestionBatch(
            null,
            checkpoint,
            [
                new SequencedMachineObservation(
                    1,
                    new MachineObservation
                    {
                        MachineId = stream.MachineId,
                        Source = "mtconnect",
                        Address = "part_count",
                        Type = SignalType.Numeric,
                        Value = 1m,
                        Quality = ObservationQuality.Good,
                        Timestamp = DateTimeOffset.UnixEpoch,
                    }),
            ],
            DateTimeOffset.UnixEpoch));

        var request = new ObservationReadRequest(stream, afterPosition: null, batchSize: 10);
        var before = await store.ReadCheckpointAsync(processor, stream);

        await using var blocker = new SqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            await using (var lockCommand = blocker.CreateCommand())
            {
                lockCommand.Transaction = transaction;
                lockCommand.CommandText =
                    "SELECT COUNT_BIG(*) FROM dbo.MachineObservation WITH (TABLOCKX,HOLDLOCK);";
                await lockCommand.ExecuteScalarAsync();
            }

            var timeout = await Assert.ThrowsAsync<SqlException>(
                async () => await store.ReadAsync(request));
            Assert.Equal(-2, timeout.Number);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Assert.Equal(before, await store.ReadCheckpointAsync(processor, stream));

        var recovered = await store.ReadAsync(request);
        var observation = Assert.Single(recovered.Observations);
        Assert.Equal(new ObservationPosition(1), observation.Position);
        Assert.Equal(stream, observation.StreamId);
        Assert.False(recovered.HasMore);
        Assert.Equal(before, await store.ReadCheckpointAsync(processor, stream));
    }

    [Fact]
    public void ObservationReadTimeoutOverrideRejectsNonPositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqlServerObservationIngestionStore("Server=localhost;", 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqlServerObservationIngestionStore("Server=localhost;", -1));
    }
}
