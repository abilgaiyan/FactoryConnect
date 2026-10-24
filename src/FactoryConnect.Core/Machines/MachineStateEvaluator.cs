using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public static class MachineStateEvaluator
{
    public static MachineState Evaluate(MachineSignalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // An explicit atomic status owns the result, including unavailable/invalid input.
        // Never fall back to stale Boolean evidence from a previous configuration.
        var operational = snapshot.Signals.Where(signal => string.Equals(
            signal.Key, CanonicalSignalKeys.OperationalState, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (operational.Length != 0)
        {
            if (operational.Length != 1 || operational[0].Quality != ObservationQuality.Good ||
                operational[0].Type != SignalType.Enumeration)
            {
                return MachineState.Unknown;
            }

            return operational[0].Value switch
            {
                "RUNNING" => MachineState.Running,
                "IDLE" => MachineState.Idle,
                "FAULT" => MachineState.Fault,
                "STOPPED" => MachineState.Stopped,
                _ => MachineState.Unknown,
            };
        }

        if (TryGetDigitalSignal(snapshot, CanonicalSignalKeys.Fault, out var fault) && fault)
        {
            return MachineState.Fault;
        }

        if (TryGetDigitalSignal(snapshot, CanonicalSignalKeys.Running, out var running) && running)
        {
            return MachineState.Running;
        }

        if (TryGetDigitalSignal(snapshot, CanonicalSignalKeys.Idle, out var idle) && idle)
        {
            return MachineState.Idle;
        }

        if (TryGetDigitalSignal(snapshot, CanonicalSignalKeys.Running, out _))
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
            candidate => candidate.Quality == ObservationQuality.Good &&
                string.Equals(
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
