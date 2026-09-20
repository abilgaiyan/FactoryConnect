using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerObservationProcessingCheckpointConcurrencyTests(
    SqlServerTestDatabaseFixture fixture) :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    [Fact]
    public async Task ConcurrentInitialCheckpointCasAdmitsExactlyOneWriter()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "MTConnect:CNC-CAS");
        var store = new SqlServerObservationIngestionStore(
            fixture.ConnectionString);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 1, 3),
                [
                    new SequencedMachineObservation(
                        1,
                        Observation(streamId.MachineId, "execution")),
                    new SequencedMachineObservation(
                        2,
                        Observation(streamId.MachineId, "load")),
                ],
                DateTimeOffset.UnixEpoch));

        var processorId = new ObservationProcessorId("canonical-mapping");
        var first = new ObservationProcessingCheckpoint(
            processorId,
            streamId,
            new ObservationPosition(1));
        var second = new ObservationProcessingCheckpoint(
            processorId,
            streamId,
            new ObservationPosition(2));
        var checkpoints = (IObservationProcessingCheckpointStore)store;

        var attempts = await Task.WhenAll(
            AttemptAsync(
                () => checkpoints.CommitAsync(
                    new ObservationProcessingCommit(null, first)).AsTask()),
            AttemptAsync(
                () => checkpoints.CommitAsync(
                    new ObservationProcessingCommit(null, second)).AsTask()));

        Assert.Single(attempts.Where(static result => result is null));
        Assert.Single(
            attempts.Where(static result =>
                result is InvalidOperationException));

        var committed = await checkpoints.ReadCheckpointAsync(
            processorId,
            streamId);
        Assert.True(committed == first || committed == second);
    }

    private static async Task<Exception?> AttemptAsync(Func<Task> action)
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

    private static MachineObservation Observation(
        MachineId machineId,
        string address) =>
        new()
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = address,
            Type = SignalType.Enumeration,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
}
