using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core;

public sealed class ProductionContextProcessingRuntime
{
    private readonly IProductionContextActivityReader _activityReader;
    private readonly IProductionContextReader _contextReader;
    private readonly ShiftOccurrenceResolver _shiftResolver;
    private readonly PlannedProductionIntervalResolver _plannedResolver;
    private readonly IProductionContextProcessingStore _store;
    private readonly ProductionContextProcessingScope _scope;
    private readonly int _batchSize;
    private ObservationProcessingCheckpoint? _checkpoint;
    private bool _checkpointRestored;

    public ProductionContextProcessingRuntime(
        ObservationProcessorId processorId,
        IProductionContextActivityReader activityReader,
        IProductionContextReader contextReader,
        ShiftOccurrenceResolver shiftResolver,
        PlannedProductionIntervalResolver plannedResolver,
        IProductionContextProcessingStore store,
        ProductionContextProcessingScope scope,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(activityReader);
        ArgumentNullException.ThrowIfNull(contextReader);
        ArgumentNullException.ThrowIfNull(shiftResolver);
        ArgumentNullException.ThrowIfNull(plannedResolver);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        scope.Validate();

        if (string.IsNullOrWhiteSpace(processorId.Value))
        {
            throw new ArgumentException("Processor ID is required.", nameof(processorId));
        }

        ProcessorId = processorId;
        _activityReader = activityReader;
        _contextReader = contextReader;
        _shiftResolver = shiftResolver;
        _plannedResolver = plannedResolver;
        _store = store;
        _scope = scope;
        _batchSize = batchSize;
    }

    public ObservationProcessorId ProcessorId { get; }

    public async Task<int> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        await RestoreCheckpointAsync(cancellationToken);

        var batch = await _activityReader.ReadAsync(
            _scope.StreamId,
            _checkpoint?.Position,
            _batchSize,
            cancellationToken);

        if (batch.Count == 0)
        {
            return 0;
        }

        ValidateBatch(batch);

        var contextualized = new List<ContextualizedActivityInterval>();
        var eligibility = new List<ProductionTimeEligibilityInterval>();
        var resolvedShifts = new List<ShiftOccurrence>();

        foreach (var source in batch)
        {
            var period = source.Period;
            var contexts = await _contextReader.ReadAsync(
                _scope.MachineId,
                period.StartedAt,
                period.EndedAt,
                cancellationToken);

            var factoryDateFrom = DateOnly.FromDateTime(period.StartedAt.UtcDateTime).AddDays(-1);
            var factoryDateTo = DateOnly.FromDateTime(period.EndedAt.UtcDateTime).AddDays(2);

            var shifts = await _shiftResolver.ResolveAsync(
                _scope.SiteId,
                _scope.ProductionLineId,
                factoryDateFrom,
                factoryDateTo,
                cancellationToken);
            resolvedShifts.AddRange(shifts);

            var activityIntervals = ActivityContextIntervalAllocator.Allocate(
                source,
                shifts,
                contexts);
            contextualized.AddRange(activityIntervals);

            var planned = await _plannedResolver.ResolveAsync(
                _scope.SiteId,
                _scope.ProductionLineId,
                factoryDateFrom,
                factoryDateTo,
                cancellationToken);

            foreach (var interval in activityIntervals)
            {
                eligibility.AddRange(
                    ProductionTimeEligibilityAllocator.Allocate(interval, planned));
            }
        }

        var metricFacts = DurableMetricInputFactDeriver.Derive(eligibility, []);
        var metricInputs = MetricInputAppendFactory.Create(
            MetricInputStreamId.ForMachine(_scope.MachineId),
            metricFacts,
            resolvedShifts
                .Distinct()
                .ToArray());
        var nextCheckpoint = new ObservationProcessingCheckpoint(
            ProcessorId,
            _scope.StreamId,
            batch[^1].Position);

        await _store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = _checkpoint,
                NextCheckpoint = nextCheckpoint,
                ContextualizedActivity = contextualized,
                EligibilityIntervals = eligibility,
                MetricFacts = metricFacts,
                MetricInputs = metricInputs,
            },
            cancellationToken);

        _checkpoint = nextCheckpoint;
        return batch.Count;
    }

    private async ValueTask RestoreCheckpointAsync(CancellationToken cancellationToken)
    {
        if (_checkpointRestored)
        {
            return;
        }

        _checkpoint = await _store.ReadCheckpointAsync(
            ProcessorId,
            _scope.StreamId,
            cancellationToken);
        _checkpointRestored = true;
    }

    private void ValidateBatch(IReadOnlyList<DurableMachineActivityPeriod> batch)
    {
        ObservationPosition? previous = null;
        foreach (var source in batch)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.StreamId != _scope.StreamId || source.Period.MachineId != _scope.MachineId)
            {
                throw new InvalidOperationException("Durable activity batch contains an item outside the configured processing scope.");
            }

            if (previous is not null && source.Position <= previous)
            {
                throw new InvalidOperationException("Durable activity batch positions must be strictly increasing.");
            }

            previous = source.Position;
        }
    }
}
