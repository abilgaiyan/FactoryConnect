using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public static class MetricInputContributionAggregator
{
    public static MetricAggregateContributionSet Aggregate(
        MetricInputStreamId streamId,
        IReadOnlyList<PositionedMetricInputFact> inputs)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(inputs);

        var ordered = ValidateAndOrder(streamId, inputs);
        var shiftGroups = new Dictionary<ShiftMetricAggregateKey, AggregateAccumulator>();
        var dayGroups = new Dictionary<ProductionDayMetricAggregateKey, AggregateAccumulator>();

        foreach (var input in ordered)
        {
            var fact = input.Fact;
            var shiftKey = new ShiftMetricAggregateKey(
                fact.MachineId,
                input.ShiftOccurrenceId,
                fact.Key);
            var dayKey = new ProductionDayMetricAggregateKey(
                fact.MachineId,
                input.ProductionDayId,
                fact.Key);

            Add(shiftGroups, shiftKey, fact);
            Add(dayGroups, dayKey, fact);
        }

        var shiftContributions = shiftGroups
            .OrderBy(static pair => pair.Key.ShiftOccurrenceId.StartsAtUtc)
            .ThenBy(static pair => pair.Key.ShiftOccurrenceId.EndsAtUtc)
            .ThenBy(static pair => pair.Key.MetricInputKey, StringComparer.Ordinal)
            .Select(static pair => new ShiftMetricAggregateContribution(
                pair.Key,
                pair.Value.ToValue()))
            .ToArray();

        var productionDayContributions = dayGroups
            .OrderBy(static pair => pair.Key.ProductionDayId.BusinessDate)
            .ThenBy(static pair => pair.Key.MetricInputKey, StringComparer.Ordinal)
            .Select(static pair => new ProductionDayMetricAggregateContribution(
                pair.Key,
                pair.Value.ToValue()))
            .ToArray();

        return new MetricAggregateContributionSet(
            shiftContributions,
            productionDayContributions);
    }

    private static PositionedMetricInputFact[] ValidateAndOrder(
        MetricInputStreamId streamId,
        IReadOnlyList<PositionedMetricInputFact> inputs)
    {
        var positions = new HashSet<MetricInputPosition>();
        var factIds = new HashSet<MetricInputFactId>();

        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);

            if (input.StreamId != streamId || input.Fact.MachineId != streamId.MachineId)
            {
                throw new ArgumentException(
                    "Metric input must belong to the configured metric input stream and machine.",
                    nameof(inputs));
            }

            if (!positions.Add(input.Position))
            {
                throw new ArgumentException(
                    "Metric input positions must be unique within an aggregation set.",
                    nameof(inputs));
            }

            if (!factIds.Add(input.Fact.Id))
            {
                throw new ArgumentException(
                    "Metric input fact identities must be unique within an aggregation set.",
                    nameof(inputs));
            }
        }

        return inputs
            .OrderBy(static input => input.Position.Value)
            .ThenBy(static input => input.Fact.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Add<TKey>(
        Dictionary<TKey, AggregateAccumulator> groups,
        TKey key,
        DurableMetricInputFact fact)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var accumulator))
        {
            groups.Add(key, AggregateAccumulator.From(fact));
            return;
        }

        accumulator.Add(fact);
    }

    private sealed class AggregateAccumulator
    {
        private decimal _value;
        private long _inputCount;
        private DateTimeOffset _firstInputTimestamp;
        private DateTimeOffset _lastInputTimestamp;

        private AggregateAccumulator(
            decimal value,
            string unit,
            DateTimeOffset firstInputTimestamp,
            DateTimeOffset lastInputTimestamp)
        {
            _value = value;
            Unit = unit;
            _inputCount = 1;
            _firstInputTimestamp = firstInputTimestamp;
            _lastInputTimestamp = lastInputTimestamp;
        }

        private string Unit { get; }

        public static AggregateAccumulator From(DurableMetricInputFact fact) =>
            new(
                fact.Value,
                fact.Unit,
                fact.StartsAtUtc,
                fact.EndsAtUtc);

        public void Add(DurableMetricInputFact fact)
        {
            if (!string.Equals(Unit, fact.Unit, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Metric inputs with the same aggregate key must use the same unit.");
            }

            _value = checked(_value + fact.Value);
            _inputCount = checked(_inputCount + 1);

            if (fact.StartsAtUtc < _firstInputTimestamp)
            {
                _firstInputTimestamp = fact.StartsAtUtc;
            }

            if (fact.EndsAtUtc > _lastInputTimestamp)
            {
                _lastInputTimestamp = fact.EndsAtUtc;
            }
        }

        public MetricAggregateValue ToValue() =>
            new(
                _value,
                Unit,
                _inputCount,
                _firstInputTimestamp,
                _lastInputTimestamp);
    }
}
