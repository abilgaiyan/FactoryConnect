using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineStateTrackerTests
{
    [Fact]
    public void TrackCreatesTransitionFromUnknownForFirstObservedState()
    {
        var tracker = new MachineStateTracker();
        var machineId = MachineId.New();
        var timestamp = DateTimeOffset.UtcNow;

        var transition = tracker.Track(
            machineId,
            MachineState.Running,
            timestamp);

        Assert.NotNull(transition);
        Assert.Equal(MachineState.Unknown, transition.PreviousState);
        Assert.Equal(MachineState.Running, transition.CurrentState);
        Assert.Equal(timestamp, transition.Timestamp);
    }

    [Fact]
    public void TrackReturnsNullWhenStateHasNotChanged()
    {
        var tracker = new MachineStateTracker();
        var machineId = MachineId.New();

        tracker.Track(
            machineId,
            MachineState.Running,
            DateTimeOffset.UtcNow);

        var transition = tracker.Track(
            machineId,
            MachineState.Running,
            DateTimeOffset.UtcNow);

        Assert.Null(transition);
    }

    [Fact]
    public void TrackCreatesTransitionWhenStateChanges()
    {
        var tracker = new MachineStateTracker();
        var machineId = MachineId.New();

        tracker.Track(
            machineId,
            MachineState.Stopped,
            DateTimeOffset.UtcNow);

        var transition = tracker.Track(
            machineId,
            MachineState.Fault,
            DateTimeOffset.UtcNow);

        Assert.NotNull(transition);
        Assert.Equal(MachineState.Stopped, transition.PreviousState);
        Assert.Equal(MachineState.Fault, transition.CurrentState);
    }
}
