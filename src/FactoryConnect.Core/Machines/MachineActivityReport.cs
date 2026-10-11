using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed record ActivityReportInterval(
    string Position, string State, DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc, DateTimeOffset SourceStartedAtUtc,
    DateTimeOffset SourceEndedAtUtc, long DurationTicks, string InstanceId, string Sequence);

public sealed record ActivityReportTotal(string State, long DurationTicks);

public sealed record MachineActivityReport(
    string MachineId, string StateProcessorId, string StreamKey, string? ThroughPosition,
    DateTimeOffset FromInclusive, DateTimeOffset ToExclusive, DateTimeOffset ReadAsOf,
    IReadOnlyList<ActivityReportInterval> Intervals,
    IReadOnlyList<ActivityReportTotal> Totals, long UnrepresentedTicks);

/// <summary>Summarizes stored intervals only; it does not certify temporal coverage.</summary>
public static class MachineActivityReportComposer
{
    public static MachineActivityReport Compose(
        CurrentStateAuthorityBinding binding, ObservationPosition? through,
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset readAsOf,
        IReadOnlyList<DurableMachineActivityPeriod> source)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(source);
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || to <= from ||
            to - from > TimeSpan.FromDays(31))
        {
            throw new ArgumentException("Select a positive UTC interval of at most 31 days.");
        }

        var intervals = new List<ActivityReportInterval>();
        ObservationPosition? previous = null;
        foreach (var item in source)
        {
            if (item.ProcessorId != binding.StateProcessorId || item.StreamId != binding.ObservationStreamId ||
                through is null || item.Position > through || (previous is not null && item.Position <= previous))
            {
                throw new InvalidDataException("Activity identity or position disagrees with the pinned read.");
            }
            previous = item.Position;
            var period = item.Period;
            if (!Enum.IsDefined(period.State) || period.EndedAt < period.StartedAt)
            {
                throw new InvalidDataException("Invalid stored activity interval.");
            }
            var start = period.StartedAt > from ? period.StartedAt.ToUniversalTime() : from;
            var end = period.EndedAt < to ? period.EndedAt.ToUniversalTime() : to;
            if (end <= start) continue;
            intervals.Add(new ActivityReportInterval(item.Position.ToString(),
                period.State.ToString().ToLowerInvariant(), start, end,
                period.StartedAt.ToUniversalTime(), period.EndedAt.ToUniversalTime(),
                (end - start).Ticks, item.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        intervals.Sort((a, b) => a.StartedAtUtc.CompareTo(b.StartedAtUtc));
        var cursor = from;
        long represented = 0;
        foreach (var interval in intervals)
        {
            if (interval.StartedAtUtc < cursor)
                throw new InvalidDataException("Overlapping stored intervals cannot produce duration totals.");
            cursor = interval.EndedAtUtc;
            represented = checked(represented + interval.DurationTicks);
        }
        var totals = Enum.GetValues<MachineState>().Select(state => new ActivityReportTotal(
            state.ToString().ToLowerInvariant(), intervals.Where(i => string.Equals(i.State, state.ToString(), StringComparison.OrdinalIgnoreCase))
                .Sum(i => i.DurationTicks))).ToArray();
        return new MachineActivityReport(binding.MachineId.ToString(), binding.StateProcessorId.Value,
            binding.ObservationStreamId.StreamKey, through?.ToString(), from, to, readAsOf,
            intervals.AsReadOnly(), Array.AsReadOnly(totals), checked((to - from).Ticks - represented));
    }
}
