using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineActivityTrackerTests
{
    [Fact]
    public void FirstStateChangeStartsActivityWithoutCompletingPeriod()
    {
        var tracker = new MachineActivityTracker();
        var machineId = MachineId.New();

        var period = tracker.Track(new MachineStateChangedEvent(
            machineId,
            MachineState.Unknown,
            MachineState.Running,
            At(10, 0)));

        Assert.Null(period);
    }

    [Fact]
    public void NextStateChangeCompletesPreviousActivityPeriod()
    {
        var tracker = new MachineActivityTracker();
        var machineId = MachineId.New();

        tracker.Track(new MachineStateChangedEvent(
            machineId,
            MachineState.Unknown,
            MachineState.Running,
            At(10, 0)));

        var period = tracker.Track(new MachineStateChangedEvent(
            machineId,
            MachineState.Running,
            MachineState.Idle,
            At(10, 12)));

        Assert.NotNull(period);
        Assert.Equal(machineId, period.MachineId);
        Assert.Equal(MachineState.Running, period.State);
        Assert.Equal(At(10, 0), period.StartedAt);
        Assert.Equal(At(10, 12), period.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(12), period.Duration);
    }

    [Fact]
    public void TrackMaintainsIndependentActivityForEachMachine()
    {
        var tracker = new MachineActivityTracker();
        var firstMachine = MachineId.New();
        var secondMachine = MachineId.New();

        tracker.Track(new MachineStateChangedEvent(
            firstMachine,
            MachineState.Unknown,
            MachineState.Running,
            At(10, 0)));

        tracker.Track(new MachineStateChangedEvent(
            secondMachine,
            MachineState.Unknown,
            MachineState.Idle,
            At(10, 5)));

        var firstPeriod = tracker.Track(new MachineStateChangedEvent(
            firstMachine,
            MachineState.Running,
            MachineState.Idle,
            At(10, 10)));

        var secondPeriod = tracker.Track(new MachineStateChangedEvent(
            secondMachine,
            MachineState.Idle,
            MachineState.Running,
            At(10, 20)));

        Assert.NotNull(firstPeriod);
        Assert.Equal(MachineState.Running, firstPeriod.State);
        Assert.Equal(TimeSpan.FromMinutes(10), firstPeriod.Duration);

        Assert.NotNull(secondPeriod);
        Assert.Equal(MachineState.Idle, secondPeriod.State);
        Assert.Equal(TimeSpan.FromMinutes(15), secondPeriod.Duration);
    }

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 8, 20, hour, minute, 0, TimeSpan.Zero);
}
