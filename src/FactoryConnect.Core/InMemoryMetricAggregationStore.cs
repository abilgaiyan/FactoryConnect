using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryMetricAggregationStore :
    IMetricAggregationStore,
    IOperationalMetricComponentSnapshotReader
{
    private readonly object _sync = new();
    private readonly Dictionary<MetricAggregationProcessorId, MetricAggregationCheckpoint> _checkpoints = [];
    private readonly Dictionary<(MetricAggregationProcessorId ProcessorId, MetricInputFactId FactId), PositionedMetricInputFact> _contributions = [];
    private readonly Dictionary<(MetricAggregationProcessorId ProcessorId, MetricInputPosition Position), MetricInputFactId> _positions = [];
    private readonly Dictionary<(MetricAggregationProcessorId ProcessorId, ShiftMetricAggregateKey Key), MetricAggregateValue> _shiftAggregates = [];
    private readonly Dictionary<(MetricAggregationProcessorId ProcessorId, ProductionDayMetricAggregateKey Key), MetricAggregateValue> _productionDayAggregates = [];

    public ValueTask<MetricAggregationCheckpoint?> ReadCheckpointAsync(
        MetricAggregationProcessorId processorId,
        MetricInputStreamId streamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_checkpoints.TryGetValue(processorId, out var checkpoint))
            {
                if (checkpoint.StreamId != streamId)
                {
                    throw new InvalidOperationException(
                        "Aggregation processor checkpoint belongs to a different metric input stream.");
                }

                return ValueTask.FromResult<MetricAggregationCheckpoint?>(checkpoint);
            }

            return ValueTask.FromResult<MetricAggregationCheckpoint?>(null);
        }
    }

    public ValueTask<MetricAggregateValue?> ReadShiftAggregateAsync(
        MetricAggregationProcessorId processorId,
        ShiftMetricAggregateKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _shiftAggregates.TryGetValue((processorId, key), out var value);
            return ValueTask.FromResult<MetricAggregateValue?>(value);
        }
    }

    public ValueTask<MetricAggregateValue?> ReadProductionDayAggregateAsync(
        MetricAggregationProcessorId processorId,
        ProductionDayMetricAggregateKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _productionDayAggregates.TryGetValue((processorId, key), out var value);
            return ValueTask.FromResult<MetricAggregateValue?>(value);
        }
    }

    public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
        OperationalMetricComponentSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_checkpoints.TryGetValue(request.ProcessorId, out var revision))
            {
                throw new InvalidOperationException(
                    "Operational metric component snapshots require an existing aggregation checkpoint.");
            }

            if (revision.StreamId.MachineId != request.EvaluationKey.MachineId)
            {
                throw new InvalidOperationException(
                    "Aggregation checkpoint belongs to a different machine stream than the requested evaluation.");
            }

            var components = new List<OperationalMetricComponent>(request.Operands.Count);
            foreach (var operand in request.Operands)
            {
                var source = (OperationalMetricOperandSource.Component)operand.Source;
                var aggregate = ReadAggregateUnderLock(
                    request.ProcessorId,
                    request.EvaluationKey,
                    source.ComponentKey);

                if (aggregate is null)
                {
                    continue;
                }

                components.Add(new OperationalMetricComponent(
                    operand.OperandName,
                    new OperationalMetricAggregateSourceIdentity(
                        request.ProcessorId,
                        request.EvaluationKey.MachineId,
                        request.EvaluationKey.PeriodId,
                        source.ComponentKey),
                    operand.RequiredDimension,
                    aggregate));
            }

            return ValueTask.FromResult(new OperationalMetricComponentSnapshot(
                request.EvaluationKey,
                revision,
                components));
        }
    }

    public ValueTask CommitAsync(
        MetricAggregationCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _checkpoints.TryGetValue(commit.ProcessorId, out var current);
            if (!CheckpointEquals(current, commit.ExpectedCheckpoint))
            {
                throw new InvalidOperationException("Metric aggregation checkpoint conflict.");
            }

            if (current is not null && current.StreamId != commit.ProposedCheckpoint.StreamId)
            {
                throw new InvalidOperationException(
                    "Aggregation processor cannot change metric input streams.");
            }

            var newInputs = StageNewInputs(commit);
            var contributions = MetricInputContributionAggregator.Aggregate(
                commit.ProposedCheckpoint.StreamId,
                newInputs);
            var stagedShift = StageShiftAggregates(commit.ProcessorId, contributions.ShiftContributions);
            var stagedProductionDay = StageProductionDayAggregates(
                commit.ProcessorId,
                contributions.ProductionDayContributions);

            foreach (var input in newInputs)
            {
                _contributions.Add((commit.ProcessorId, input.Fact.Id), input);
                _positions.Add((commit.ProcessorId, input.Position), input.Fact.Id);
            }

            foreach (var pair in stagedShift)
            {
                _shiftAggregates[pair.Key] = pair.Value;
            }

            foreach (var pair in stagedProductionDay)
            {
                _productionDayAggregates[pair.Key] = pair.Value;
            }

            _checkpoints[commit.ProcessorId] = commit.ProposedCheckpoint;
            return ValueTask.CompletedTask;
        }
    }

    private MetricAggregateValue? ReadAggregateUnderLock(
        MetricAggregationProcessorId processorId,
        OperationalMetricEvaluationKey evaluationKey,
        string componentKey) =>
        evaluationKey.PeriodId switch
        {
            OperationalMetricPeriodId.Shift shift =>
                _shiftAggregates.TryGetValue(
                    (processorId, new ShiftMetricAggregateKey(
                        evaluationKey.MachineId,
                        shift.ShiftOccurrenceId,
                        componentKey)),
                    out var shiftValue)
                        ? shiftValue
                        : null,
            OperationalMetricPeriodId.ProductionDay productionDay =>
                _productionDayAggregates.TryGetValue(
                    (processorId, new ProductionDayMetricAggregateKey(
                        evaluationKey.MachineId,
                        productionDay.ProductionDayId,
                        componentKey)),
                    out var productionDayValue)
                        ? productionDayValue
                        : null,
            _ => throw new InvalidOperationException("Unsupported operational metric period type."),
        };

    private List<PositionedMetricInputFact> StageNewInputs(MetricAggregationCommit commit)
    {
        var staged = new List<PositionedMetricInputFact>();
        var seenFacts = new HashSet<MetricInputFactId>();
        var seenPositions = new HashSet<MetricInputPosition>();

        foreach (var input in commit.Inputs)
        {
            if (input.Position > commit.ProposedCheckpoint.Position)
            {
                throw new InvalidOperationException(
                    "Metric input position cannot exceed the proposed aggregation checkpoint.");
            }

            if (!seenFacts.Add(input.Fact.Id))
            {
                throw new InvalidOperationException(
                    "Aggregation commit contains a duplicate metric input fact identity.");
            }

            if (!seenPositions.Add(input.Position))
            {
                throw new InvalidOperationException(
                    "Aggregation commit contains a duplicate metric input position.");
            }

            var factIdentity = (commit.ProcessorId, input.Fact.Id);
            if (_contributions.TryGetValue(factIdentity, out var persisted))
            {
                if (persisted != input)
                {
                    throw new InvalidOperationException(
                        "Metric input fact identity was replayed with conflicting content or ownership.");
                }

                continue;
            }

            if (commit.ExpectedCheckpoint is not null &&
                input.Position <= commit.ExpectedCheckpoint.Position)
            {
                throw new InvalidOperationException(
                    "New metric input position must be after the expected aggregation checkpoint.");
            }

            var positionIdentity = (commit.ProcessorId, input.Position);
            if (_positions.TryGetValue(positionIdentity, out var persistedFactId) &&
                persistedFactId != input.Fact.Id)
            {
                throw new InvalidOperationException(
                    "Metric input position was reused for a different fact identity.");
            }

            staged.Add(input);
        }

        return staged;
    }

    private Dictionary<(MetricAggregationProcessorId ProcessorId, ShiftMetricAggregateKey Key), MetricAggregateValue> StageShiftAggregates(
        MetricAggregationProcessorId processorId,
        IReadOnlyList<ShiftMetricAggregateContribution> contributions)
    {
        var staged = new Dictionary<(MetricAggregationProcessorId, ShiftMetricAggregateKey), MetricAggregateValue>();

        foreach (var contribution in contributions)
        {
            var identity = (processorId, contribution.Key);
            _shiftAggregates.TryGetValue(identity, out var current);
            staged.Add(identity, Merge(current, contribution.Value));
        }

        return staged;
    }

    private Dictionary<(MetricAggregationProcessorId ProcessorId, ProductionDayMetricAggregateKey Key), MetricAggregateValue> StageProductionDayAggregates(
        MetricAggregationProcessorId processorId,
        IReadOnlyList<ProductionDayMetricAggregateContribution> contributions)
    {
        var staged = new Dictionary<(MetricAggregationProcessorId, ProductionDayMetricAggregateKey), MetricAggregateValue>();

        foreach (var contribution in contributions)
        {
            var identity = (processorId, contribution.Key);
            _productionDayAggregates.TryGetValue(identity, out var current);
            staged.Add(identity, Merge(current, contribution.Value));
        }

        return staged;
    }

    private static MetricAggregateValue Merge(
        MetricAggregateValue? current,
        MetricAggregateValue contribution)
    {
        if (current is null)
        {
            return contribution;
        }

        if (!string.Equals(current.Unit, contribution.Unit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Persisted aggregate and contribution units are incompatible.");
        }

        return new MetricAggregateValue(
            checked(current.Value + contribution.Value),
            current.Unit,
            checked(current.InputCount + contribution.InputCount),
            current.FirstInputTimestamp <= contribution.FirstInputTimestamp
                ? current.FirstInputTimestamp
                : contribution.FirstInputTimestamp,
            current.LastInputTimestamp >= contribution.LastInputTimestamp
                ? current.LastInputTimestamp
                : contribution.LastInputTimestamp);
    }

    private static bool CheckpointEquals(
        MetricAggregationCheckpoint? left,
        MetricAggregationCheckpoint? right) =>
        left is null
            ? right is null
            : right is not null && left == right;
}
