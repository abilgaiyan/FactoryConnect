using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

/// <summary>
/// Validates a supervisor's proposed downtime explanation against a durable activity
/// period obtained from the authoritative machine activity reader. This check alone
/// does not authorize writes or qualify OEE.
/// </summary>
public static class SupervisorDowntimeExplanationEligibility
{
    public static bool Matches(
        DurableMachineActivityPeriod? authoritative,
        Guid machineId,
        string stateProcessorId,
        long observationPosition,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        if (authoritative is null || machineId == Guid.Empty ||
            string.IsNullOrWhiteSpace(stateProcessorId) || observationPosition <= 0 ||
            startsAtUtc.Offset != TimeSpan.Zero || endsAtUtc.Offset != TimeSpan.Zero ||
            startsAtUtc >= endsAtUtc)
            return false;

        var period = authoritative.Period;
        return period.MachineId.Value == machineId &&
            string.Equals(authoritative.ProcessorId.Value, stateProcessorId, StringComparison.Ordinal) &&
            authoritative.Position.Value == (ulong)observationPosition &&
            period.StartedAt == startsAtUtc && period.EndedAt == endsAtUtc &&
            period.State is MachineState.Stopped or MachineState.Fault or MachineState.Offline;
    }
}
