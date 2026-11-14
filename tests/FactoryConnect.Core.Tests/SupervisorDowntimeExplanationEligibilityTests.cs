using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class SupervisorDowntimeExplanationEligibilityTests
{
    [Theory]
    [InlineData(MachineState.Stopped, true)]
    [InlineData(MachineState.Fault, true)]
    [InlineData(MachineState.Offline, true)]
    [InlineData(MachineState.Running, false)]
    [InlineData(MachineState.Idle, false)]
    [InlineData(MachineState.Unknown, false)]
    public void OnlyExactDurableDowntimeIsEligible(MachineState state, bool expected)
    {
        var machineId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(5);
        var durable = new DurableMachineActivityPeriod(
            new ObservationProcessorId("machine-state-activity"),
            new ObservationPosition(12),
            new ObservationStreamId(new MachineId(machineId), "execution"),
            1, 12,
            new MachineActivityPeriod(new MachineId(machineId), state, start, end));

        bool Eligible(Guid machine, string processor, long position, DateTimeOffset from, DateTimeOffset to) =>
            SupervisorDowntimeExplanationEligibility.Matches(durable, machine, processor, position, from, to);

        Assert.Equal(expected, Eligible(machineId, "machine-state-activity", 12, start, end));
        Assert.False(Eligible(Guid.NewGuid(), "machine-state-activity", 12, start, end));
        Assert.False(Eligible(machineId, "other-processor", 12, start, end));
        Assert.False(Eligible(machineId, "machine-state-activity", 13, start, end));
        Assert.False(Eligible(machineId, "machine-state-activity", 12, start.AddSeconds(1), end));
        Assert.False(Eligible(machineId, "machine-state-activity", 12, start, end.AddSeconds(1)));
        Assert.False(SupervisorDowntimeExplanationEligibility.Matches(null, machineId,
            "machine-state-activity", 12, start, end));
    }
}
