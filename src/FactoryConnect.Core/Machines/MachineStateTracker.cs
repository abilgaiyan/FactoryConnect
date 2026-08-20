using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed class MachineStateTracker
{
    private readonly Dictionary<MachineId, MachineState> _states = [];

    public MachineStateChangedEvent? Track(
        MachineId machineId,
        MachineState currentState,
        DateTimeOffset timestamp)
    {
        var previousState = _states.GetValueOrDefault(
            machineId,
            MachineState.Unknown);

        _states[machineId] = currentState;

        if (previousState == currentState)
        {
            return null;
        }

        return new MachineStateChangedEvent(
            machineId,
            previousState,
            currentState,
            timestamp);
    }
}
