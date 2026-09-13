using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class MachineStateActivityCompositionFoundationConformanceTests
{
    private static readonly ObservationProcessorId ProcessorId =
        new("machine-state-activity");

    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CursorReadsJointProjectionPositionAndReturnsNullWithoutAuthority()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        IMachineStateActivityCursorReader reader =
            new JointMachineStateActivityCursorReader(store);
        var stream = Stream("CNC-01");

        Assert.Null(await reader.ReadAsync(ProcessorId, stream));

        await PublishAsync(store, stream, 7, []);

        Assert.Equal(
            new ObservationPosition(7),
            await reader.ReadAsync(ProcessorId, stream));
    }

    [Fact]
    public void CursorReaderRejectsNullAuthorityStore()
    {
        Assert.Throws<ArgumentNullException>(
            () => new JointMachineStateActivityCursorReader(null!));
    }

    [Fact]
    public async Task CursorReadValidatesArgumentsAndHonorsCancellation()
    {
        IMachineStateActivityCursorReader reader =
            new JointMachineStateActivityCursorReader(
                new InMemoryMachineStateActivityAuthorityStore());
        var stream = Stream("CNC-01");

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await reader.ReadAsync(null!, stream));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await reader.ReadAsync(ProcessorId, null!));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.ReadAsync(ProcessorId, stream, cancellation.Token));
    }

    [Fact]
    public async Task ActivityReaderUsesJointHistoryWithStrictPagingAndStreamIsolation()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        IProductionContextActivityReader reader =
            new JointProductionContextActivityReader(store, ProcessorId);
        var target = Stream("CNC-01");
        var other = new ObservationStreamId(target.MachineId, "MTConnect:CNC-02");

        await PublishAsync(store, target, 1, [Activity(target, 1)]);
        await PublishAsync(store, target, 2, [Activity(target, 2)]);
        await PublishAsync(store, target, 3, [Activity(target, 3)]);
        await PublishAsync(store, other, 4, [Activity(other, 4)]);

        var first = await reader.ReadAsync(target, null, 2, CancellationToken.None);
        Assert.Equal([new ObservationPosition(1), new ObservationPosition(2)],
            first.Select(item => item.Position).ToArray());

        var second = await reader.ReadAsync(
            target,
            new ObservationPosition(2),
            10,
            CancellationToken.None);
        var item = Assert.Single(second);
        Assert.Equal(new ObservationPosition(3), item.Position);
        Assert.All(second, period => Assert.Equal(target, period.StreamId));
    }

    [Fact]
    public void ActivityReaderRejectsNullConstructorDependencies()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();

        Assert.Throws<ArgumentNullException>(
            () => new JointProductionContextActivityReader(null!, ProcessorId));
        Assert.Throws<ArgumentNullException>(
            () => new JointProductionContextActivityReader(store, null!));
    }

    [Fact]
    public async Task ActivityReaderValidatesArgumentsAndHonorsCancellation()
    {
        IProductionContextActivityReader reader =
            new JointProductionContextActivityReader(
                new InMemoryMachineStateActivityAuthorityStore(),
                ProcessorId);
        var stream = Stream("CNC-01");

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await reader.ReadAsync(null!, null, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await reader.ReadAsync(stream, null, 0, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.ReadAsync(stream, null, 1, cancellation.Token));
    }

    [Fact]
    public void CanonicalPreservePolicyHasFrozenIdentityModeAndStableInstance()
    {
        var first = CanonicalCurrentStateContinuityPolicies.Preserve;
        var second = CanonicalCurrentStateContinuityPolicies.Preserve;

        Assert.Same(first, second);
        Assert.Equal("continuity/preserve", first.Reference.Identity);
        Assert.Equal("1.0", first.Reference.Version);
        Assert.Equal(StateContinuityMode.Preserve, first.Mode);
    }

    private static async Task PublishAsync(
        InMemoryMachineStateActivityAuthorityStore store,
        ObservationStreamId stream,
        ulong position,
        IReadOnlyList<DurableMachineActivityPeriod> activities)
    {
        var current = await store.ReadAsync(ProcessorId, stream);
        var expectedPosition = current?.Projection.Position;
        var expectedRevision = current?.EvaluationAuthority.ProjectionRevision;
        var proposal = new MachineStateActivityAuthorityPublication(
            expectedPosition,
            expectedRevision,
            new MachineStateActivityProjection(
                ProcessorId,
                stream,
                new ObservationPosition(position),
                [],
                MachineState.Unknown,
                null,
                null),
            [],
            activities,
            new EvaluationAuthorityReplayIdentity(
                ProcessorId,
                stream,
                new ObservationPosition(position),
                MachineState.Unknown,
                1,
                CanonicalCurrentStateContinuityPolicies.Preserve.Reference));

        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(proposal));
    }

    private static DurableMachineActivityPeriod Activity(
        ObservationStreamId stream,
        ulong position) =>
        new(
            ProcessorId,
            new ObservationPosition(position),
            stream,
            1,
            position,
            new MachineActivityPeriod(
                stream.MachineId,
                MachineState.Running,
                Stamp.AddSeconds(position - 1),
                Stamp.AddSeconds(position)));

    private static ObservationStreamId Stream(string source) =>
        new(MachineId.New(), $"MTConnect:{source}");
}
