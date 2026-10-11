using System.Globalization;
using System.Numerics;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed record CounterSignal(string Source, string Address);
public sealed record CounterSample(string Position, string InstanceId, string Sequence,
    DateTimeOffset ObservedAtUtc, string SignalType, string Quality, string? RawValue,
    string? PreviousPosition, string? ArithmeticDifference, IReadOnlyList<string> Flags);
public sealed record MachineCounterReport(Guid MachineId, string StreamKey, string ThroughPosition,
    string AcquisitionRevision, DateTimeOffset FromInclusive, DateTimeOffset ToExclusive,
    DateTimeOffset ReadAsOf, string Source, string Address, IReadOnlyList<CounterSignal> AvailableSignals,
    IReadOnlyList<CounterSample> Samples);

/// <summary>Diagnostic arithmetic only; no counter continuity or production admission is inferred.</summary>
public static class MachineCounterReportComposer
{
    public static MachineCounterReport Compose(ObservationStreamId stream, ObservationPosition through,
        AcquisitionAuthorityRevision revision, DateTimeOffset from, DateTimeOffset to, DateTimeOffset readAsOf,
        string source, string address, IReadOnlyList<DurableMachineObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(through);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || to <= from || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("A positive UTC interval of at most 31 days is required.");
        var samples = new List<CounterSample>();
        var signals = new HashSet<CounterSignal>();
        DurableMachineObservation? previous = null;
        ObservationPosition? cursor = null;
        foreach (var item in observations)
        {
            if (item.StreamId != stream || item.Position <= cursor || item.Position > through)
                throw new InvalidDataException("Counter prefix identity or ordering mismatch.");
            cursor = item.Position;
            var raw = item.Observation;
            if (!Enum.IsDefined(raw.Type) || !Enum.IsDefined(raw.Quality))
                throw new InvalidDataException("Unsupported observation classification.");
            var selected = raw.Timestamp >= from && raw.Timestamp < to;
            if (selected && raw.Type is SignalType.Numeric or SignalType.Enumeration)
                signals.Add(new(raw.Source, raw.Address));
            if (!string.Equals(raw.Source, source, StringComparison.Ordinal) ||
                !string.Equals(raw.Address, address, StringComparison.Ordinal)) continue;
            var flags = new List<string> { "ContinuityNotEstablished", "AttributionNotEstablished" };
            string? difference = null;
            var valid = TryCounterValue(raw, out var number);
            if (raw.Value is null) flags.Add("MissingValue");
            if (raw.Quality != ObservationQuality.Good) flags.Add("NonGoodEvidence");
            if (!valid && raw.Value is not null) flags.Add("NotUsableAsCounter");
            if (previous is null) flags.Add("BaselineOnly");
            else
            {
                var prior = previous.Observation;
                if (item.InstanceId != previous.InstanceId) flags.Add("InstanceChanged");
                if (raw.Timestamp <= prior.Timestamp) flags.Add("NonIncreasingTimestamp");
                if (raw.Type != prior.Type) flags.Add("SignalTypeChanged");
                var priorValid = TryCounterValue(prior, out var priorNumber);
                if (!priorValid) flags.Add("PreviousEvidenceUnusable");
                if (valid && priorValid && raw.Type == prior.Type && item.InstanceId == previous.InstanceId && raw.Timestamp > prior.Timestamp)
                {
                    var change = number - priorNumber;
                    if (change < 0) flags.Add("CounterDecreased");
                    else difference = change.ToString(CultureInfo.InvariantCulture);
                }
            }
            if (selected)
                samples.Add(new(item.Position.ToString(), item.InstanceId.ToString(CultureInfo.InvariantCulture),
                    item.Sequence.ToString(CultureInfo.InvariantCulture), raw.Timestamp.ToUniversalTime(),
                    raw.Type.ToString(), raw.Quality.ToString(), RawText(raw.Value), previous?.Position.ToString(),
                    difference, flags.AsReadOnly()));
            previous = item;
        }
        return new(stream.MachineId.Value, stream.StreamKey, through.ToString(), revision.ToString(),
            from, to, readAsOf, source, address,
            Array.AsReadOnly(signals.OrderBy(s => s.Source, StringComparer.Ordinal).ThenBy(s => s.Address, StringComparer.Ordinal).ToArray()),
            samples.AsReadOnly());
    }

    private static bool TryCounterValue(MachineObservation observation, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (observation.Quality != ObservationQuality.Good) return false;
        if (observation.Type == SignalType.Numeric && observation.Value is decimal number &&
            number >= 0 && number == decimal.Truncate(number))
        {
            value = new BigInteger(number);
            return true;
        }
        return observation.Type == SignalType.Enumeration && observation.Value is string text &&
            text.Length > 0 && text.All(char.IsAsciiDigit) &&
            BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string? RawText(object? value) => value switch
    {
        null => null,
        decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
        string text => text,
        bool digital => digital ? "true" : "false",
        _ => throw new InvalidDataException("Unsupported raw value representation."),
    };
}
