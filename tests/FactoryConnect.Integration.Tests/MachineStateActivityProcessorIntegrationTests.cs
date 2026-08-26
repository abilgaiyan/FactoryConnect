using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class MachineStateActivityProcessorIntegrationTests
{
    [Fact]
    public async Task FirstRunningObservationCreatesStateChangeAndStartsActivity()
    {
        var context = Context();

        await context.Processor.ProcessAsync(
            [Observation(context.StreamId, 1, true)]);

        var stateChange = Assert.Single(
            context.Store.ReadStateChanges(
                context.Processor.ProcessorId,
                context.StreamId));
        Assert.Equal(MachineState.Unknown, stateChange.StateChanged.PreviousState);
        Assert.Equal(MachineState.Running, stateChange.StateChanged.CurrentState);
        Assert.Equal(new ObservationPosition(1), stateChange.Position);
        Assert.Empty(
            context.Store.ReadActivityPeriods(
                context.Processor.ProcessorId,
                context.StreamId));

        var projection = await context.Store.ReadAsync(
            context.Processor.ProcessorId,
            context.StreamId);
        Assert.NotNull(projection);
        Assert.Equal(MachineState.Running, projection.State);
        Assert.Equal(MachineState.Running, projection.ActiveState);
        Assert.Equal(At(1), projection.ActiveStartedAt);
    }

    [Fact]
    public async Task StateTransitionCompletesPreviousActivityPeriod()
    {
        var context = Context();

        await context.Processor.ProcessAsync(
            [
                Observation(context.StreamId, 1, true),
                Observation(context.StreamId, 2, false),
            ]);

        var changes = context.Store.ReadStateChanges(
            context.Processor.ProcessorId,
            context.StreamId);
        var period = Assert.Single(
            context.Store.ReadActivityPeriods(
                context.Processor.ProcessorId,
                context.StreamId));

        Assert.Equal(2, changes.Length);
        Assert.Equal(MachineState.Stopped, changes[1].StateChanged.CurrentState);
        Assert.Equal(new ObservationPosition(2), period.Position);
        Assert.Equal(MachineState.Running, period.Period.State);
        Assert.Equal(At(1), period.Period.StartedAt);
        Assert.Equal(At(2), period.Period.EndedAt);
    }

    [Fact]
    public async Task UnchangedStateAdvancesProjectionWithoutDerivedOutput()
    {
        var context = Context();

        await context.Processor.ProcessAsync(
            [
                Observation(context.StreamId, 1, true),
                Observation(context.StreamId, 2, true),
            ]);

        Assert.Single(
            context.Store.ReadStateChanges(
                context.Processor.ProcessorId,
                context.StreamId));
        Assert.Empty(
            context.Store.ReadActivityPeriods(
                context.Processor.ProcessorId,
                context.StreamId));

        var projection = await context.Store.ReadAsync(
            context.Processor.ProcessorId,
            context.StreamId);
        Assert.Equal(new ObservationPosition(2), projection?.Position);
    }

    [Fact]
    public async Task EquivalentReplayDoesNotDuplicateDerivedOutputs()
    {
        var context = Context();
        DurableMappedMachineObservation[] observations =
        [
            Observation(context.StreamId, 1, true),
            Observation(context.StreamId, 2, false),
        ];

        await context.Processor.ProcessAsync(observations);
        await context.Processor.ProcessAsync(observations);

        Assert.Equal(
            2,
            context.Store.ReadStateChanges(
                context.Processor.ProcessorId,
                context.StreamId).Length);
        Assert.Single(
            context.Store.ReadActivityPeriods(
                context.Processor.ProcessorId,
                context.StreamId));
    }

    [Fact]
    public async Task StoreRejectsStaleProjectionCommit()
    {
        var context = Context();
        await context.Processor.ProcessAsync(
            [Observation(context.StreamId, 1, true)]);
        var stale = await context.Store.ReadAsync(
            context.Processor.ProcessorId,
            context.StreamId);
        Assert.NotNull(stale);

        await context.Processor.ProcessAsync(
            [Observation(context.StreamId, 2, false)]);

        var replacement = new MachineStateActivityProjection(
            context.Processor.ProcessorId,
            context.StreamId,
            new ObservationPosition(3),
            stale.Signals,
            stale.State,
            stale.ActiveState,
            stale.ActiveStartedAt);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Store.CommitAsync(
                new MachineStateActivityProjectionCommit(
                    stale,
                    replacement,
                    [],
                    [])).AsTask());

        Assert.Equal(
            new ObservationPosition(2),
            (await context.Store.ReadAsync(
                context.Processor.ProcessorId,
                context.StreamId))?.Position);
    }

    [Fact]
    public async Task StoreFailureLeavesProcessorReadyForEquivalentRetry()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var store = new FailOnceProjectionStore();
        var processor = new MachineStateActivityProcessor(
            new ObservationProcessorId("machine-state"),
            store);
        var observation = Observation(streamId, 1, true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync([observation]).AsTask());
        await processor.ProcessAsync([observation]);

        Assert.Single(store.Inner.ReadStateChanges(
            processor.ProcessorId,
            streamId));
    }

    private static TestContext Context()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var store = new InMemoryMachineStateActivityProjectionStore();
        var processor = new MachineStateActivityProcessor(
            new ObservationProcessorId("machine-state"),
            store);

        return new TestContext(streamId, store, processor);
    }

    private static DurableMappedMachineObservation Observation(
        ObservationStreamId streamId,
        ulong position,
        bool running) =>
        new(
            new ObservationPosition(position),
            streamId,
            7,
            100 + position,
            new MappedMachineObservation
            {
                MachineId = streamId.MachineId,
                SignalKey = CanonicalSignalKeys.Running,
                Type = SignalType.Digital,
                Value = running,
                Source = "modbus",
                Address = "DI1",
                Quality = ObservationQuality.Good,
                Timestamp = At(position),
            });

    private static DateTimeOffset At(ulong minute) =>
        new(2026, 8, 26, 10, checked((int)minute), 0, TimeSpan.Zero);

    private sealed record TestContext(
        ObservationStreamId StreamId,
        InMemoryMachineStateActivityProjectionStore Store,
        MachineStateActivityProcessor Processor);

    private sealed class FailOnceProjectionStore :
        IMachineStateActivityProjectionStore
    {
        private bool _shouldFail = true;

        public InMemoryMachineStateActivityProjectionStore Inner { get; } =
            new();

        public ValueTask<MachineStateActivityProjection?> ReadAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(processorId, streamId, cancellationToken);

        public ValueTask CommitAsync(
            MachineStateActivityProjectionCommit commit,
            CancellationToken cancellationToken = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                throw new InvalidOperationException("Simulated store failure.");
            }

            return Inner.CommitAsync(commit, cancellationToken);
        }
    }
}
