using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed class MachineActivityTracker
{
    private readonly Dictionary<MachineId, ActivePeriod> _activePeriods = [];

    public MachineActivityPeriod? Track(MachineStateChangedEvent stateChanged)
    {
        ArgumentNullException.ThrowIfNull(stateChanged);

        MachineActivityPeriod? completedPeriod = null;

        if (_activePeriods.TryGetValue(stateChanged.MachineId, out var activePeriod))
        {
            completedPeriod = new MachineActivityPeriod(
                stateChanged.MachineId,
                activePeriod.State,
                activePeriod.StartedAt,
                stateChanged.Timestamp);
        }

        _activePeriods[stateChanged.MachineId] = new ActivePeriod(
            stateChanged.CurrentState,
            stateChanged.Timestamp);

        return completedPeriod;
    }

    private sealed record ActivePeriod(
        MachineState State,
        DateTimeOffset StartedAt);
}
