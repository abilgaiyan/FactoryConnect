using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public static class MachineStateEvaluator
{
    public static MachineState Evaluate(MachineSignalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (TryGetDigitalSignal(snapshot, "Fault", out var fault) && fault)
        {
            return MachineState.Fault;
        }

        if (TryGetDigitalSignal(snapshot, "Running", out var running) && running)
        {
            return MachineState.Running;
        }

        if (TryGetDigitalSignal(snapshot, "Idle", out var idle) && idle)
        {
            return MachineState.Idle;
        }

        if (TryGetDigitalSignal(snapshot, "Running", out _))
        {
            return MachineState.Stopped;
        }

        return MachineState.Unknown;
    }

    private static bool TryGetDigitalSignal(
        MachineSignalSnapshot snapshot,
        string key,
        out bool value)
    {
        var signal = snapshot.Signals.FirstOrDefault(
            candidate => string.Equals(
                candidate.Key,
                key,
                StringComparison.OrdinalIgnoreCase));

        if (signal is { Type: SignalType.Digital, Value: bool digitalValue })
        {
            value = digitalValue;
            return true;
        }

        value = false;
        return false;
    }
}
