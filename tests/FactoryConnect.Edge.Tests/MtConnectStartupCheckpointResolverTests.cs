using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectStartupCheckpointResolverTests
{
    [Fact]
    public async Task ResolveAsyncUsesBootstrapSequenceWithoutCheckpoint()
    {
        var store = new RecordingStore();
        var options = Options(fromSequence: 101);
        var resolver = new MtConnectStartupCheckpointResolver(store);

        var state = await resolver.ResolveAsync(options);

        Assert.False(state.IsRestored);
        Assert.Null(state.Checkpoint);
        Assert.Equal(101UL, state.FromSequence);
        Assert.Equal(
            MtConnectObservationStreamId.Create(
                options.MachineId,
                options.DeviceKey),
            state.StreamId);
    }

    [Fact]
    public async Task ResolveAsyncPrefersCheckpointNextSequence()
    {
        var options = Options(fromSequence: 101);
        var streamId = MtConnectObservationStreamId.Create(
            options.MachineId,
            options.DeviceKey);
        var checkpoint = new ObservationCheckpoint(
            streamId,
            instanceId: 42,
            nextSequence: 500);
        var resolver = new MtConnectStartupCheckpointResolver(
            new RecordingStore(checkpoint));

        var state = await resolver.ResolveAsync(options);

        Assert.True(state.IsRestored);
        Assert.Equal(checkpoint, state.Checkpoint);
        Assert.Equal(500UL, state.FromSequence);
    }

    [Fact]
    public async Task ResolveAsyncUsesCanonicalStreamLookup()
    {
        var options = Options(
            fromSequence: 101,
            deviceKey: " cnc-01 ");
        var store = new RecordingStore();
        var resolver = new MtConnectStartupCheckpointResolver(store);

        await resolver.ResolveAsync(options);

        Assert.Equal(
            "mtconnect:CNC-01",
            Assert.Single(store.ReadStreamIds).StreamKey);
    }

    [Fact]
    public async Task ResolveAsyncRejectsCheckpointForAnotherStream()
    {
        var options = Options(fromSequence: 101);
        var otherStream = MtConnectObservationStreamId.Create(
            options.MachineId,
            "CNC-02");
        var resolver = new MtConnectStartupCheckpointResolver(
            new RecordingStore(
                new ObservationCheckpoint(
                    otherStream,
                    instanceId: 42,
                    nextSequence: 500)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(options).AsTask());
    }

    [Fact]
    public async Task ResolveAsyncPropagatesCancellation()
    {
        var store = new RecordingStore();
        var resolver = new MtConnectStartupCheckpointResolver(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(
                Options(fromSequence: 101),
                cancellation.Token).AsTask());

        Assert.Empty(store.ReadStreamIds);
    }

    [Fact]
    public async Task ResolveAsyncPropagatesStoreFailure()
    {
        var resolver = new MtConnectStartupCheckpointResolver(
            new RecordingStore(
                failure: new InvalidOperationException(
                    "Checkpoint read failed.")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(
                Options(fromSequence: 101)).AsTask());

        Assert.Equal("Checkpoint read failed.", exception.Message);
    }

    private static MtConnectAcquisitionOptions Options(
        ulong fromSequence,
        string deviceKey = "CNC-01")
    {
        return new MtConnectAcquisitionOptions(
            new MtConnectEndpoint(
                new Uri("http://localhost:5000")),
            MachineId.New(),
            deviceKey,
            fromSequence,
            TimeSpan.FromSeconds(1));
    }

    private sealed class RecordingStore(
        ObservationCheckpoint? checkpoint = null,
        Exception? failure = null)
        : IObservationIngestionStore
    {
        public List<ObservationStreamId> ReadStreamIds { get; } = [];

        public ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadStreamIds.Add(streamId);

            if (failure is not null)
            {
                throw failure;
            }

            return ValueTask.FromResult(checkpoint);
        }

        public ValueTask CommitAsync(
            ObservationIngestionBatch batch,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
