using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public abstract record OperationalMetricProjectionEvidence
{
    private protected OperationalMetricProjectionEvidence(string operandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operandName);
        OperandName = operandName;
    }

    public string OperandName { get; }
}

public sealed record OperationalMetricComponentProjectionEvidence : OperationalMetricProjectionEvidence
{
    public OperationalMetricComponentProjectionEvidence(
        string operandName,
        OperationalMetricAggregateSourceIdentity sourceIdentity,
        MetricAggregationCheckpoint sourceRevision,
        MetricDimension dimension,
        decimal value,
        string unit,
        long inputCount,
        DateTimeOffset firstInputTimestamp,
        DateTimeOffset lastInputTimestamp)
        : base(operandName)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputCount);

        if (sourceIdentity.ProcessorId != sourceRevision.ProcessorId)
        {
            throw new ArgumentException(
                "Durable component evidence must belong to the source revision aggregation processor.",
                nameof(sourceRevision));
        }

        if (sourceRevision.StreamId.MachineId != sourceIdentity.MachineId)
        {
            throw new ArgumentException(
                "Durable component evidence source revision must belong to the source machine stream.",
                nameof(sourceRevision));
        }

        if (lastInputTimestamp < firstInputTimestamp)
        {
            throw new ArgumentException(
                "Last input timestamp must not precede the first input timestamp.",
                nameof(lastInputTimestamp));
        }

        SourceIdentity = sourceIdentity;
        SourceRevision = sourceRevision;
        Dimension = dimension;
        Value = value;
        Unit = unit;
        InputCount = inputCount;
        FirstInputTimestamp = firstInputTimestamp;
        LastInputTimestamp = lastInputTimestamp;
    }

    public OperationalMetricAggregateSourceIdentity SourceIdentity { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public MetricDimension Dimension { get; }

    public decimal Value { get; }

    public string Unit { get; }

    public long InputCount { get; }

    public DateTimeOffset FirstInputTimestamp { get; }

    public DateTimeOffset LastInputTimestamp { get; }
}

public sealed record OperationalMetricDependencyProjectionEvidence : OperationalMetricProjectionEvidence
{
    public OperationalMetricDependencyProjectionEvidence(
        string operandName,
        OperationalMetricDefinitionId definitionId,
        OperationalMetricProjection projection)
        : base(operandName)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        ArgumentNullException.ThrowIfNull(projection);

        if (projection.Key.DefinitionId != definitionId)
        {
            throw new ArgumentException(
                "Durable dependency evidence must reference the exact projected metric definition.",
                nameof(projection));
        }

        DefinitionId = definitionId;
        Projection = projection;
    }

    public OperationalMetricDefinitionId DefinitionId { get; }

    public OperationalMetricProjection Projection { get; }
}

public sealed record OperationalMetricProjection
{
    public OperationalMetricProjection(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        OperationalMetricEvaluationStatus status,
        decimal? value,
        string unit,
        OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName,
        MetricAggregationCheckpoint sourceRevision,
        IEnumerable<OperationalMetricComponentProjectionEvidence>? operandEvidence = null,
        IEnumerable<OperationalMetricDependencyProjectionEvidence>? dependencyEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(sourceRevision);

        if (sourceRevision.StreamId.MachineId != key.MachineId)
        {
            throw new ArgumentException(
                "Projection source revision must belong to the evaluation machine stream.",
                nameof(sourceRevision));
        }

        var operandSnapshot = operandEvidence?.ToArray() ?? [];
        if (operandSnapshot.Any(static evidence => evidence is null))
        {
            throw new ArgumentException(
                "Durable component evidence cannot contain null values.",
                nameof(operandEvidence));
        }

        foreach (var evidence in operandSnapshot)
        {
            if (evidence.SourceRevision != sourceRevision ||
                evidence.SourceIdentity.MachineId != key.MachineId ||
                evidence.SourceIdentity.PeriodId != key.PeriodId)
            {
                throw new ArgumentException(
                    "Durable component evidence must belong to the projection identity and coherent source revision.",
                    nameof(operandEvidence));
            }
        }

        var dependencySnapshot = dependencyEvidence?.ToArray() ?? [];
        if (dependencySnapshot.Any(static evidence => evidence is null))
        {
            throw new ArgumentException(
                "Durable dependency evidence cannot contain null values.",
                nameof(dependencyEvidence));
        }

        foreach (var evidence in dependencySnapshot)
        {
            var dependency = evidence.Projection;
            if (dependency.ProcessorId != processorId ||
                dependency.SourceRevision != sourceRevision ||
                dependency.Key.MachineId != key.MachineId ||
                dependency.Key.PeriodId != key.PeriodId ||
                dependency.Key.ContextKey != key.ContextKey)
            {
                throw new ArgumentException(
                    "Durable dependency evidence must belong to the projection processor, identity, and coherent source revision.",
                    nameof(dependencyEvidence));
            }
        }

        var duplicateEvidence = operandSnapshot
            .Select(static evidence => evidence.OperandName)
            .Concat(dependencySnapshot.Select(static evidence => evidence.OperandName))
            .GroupBy(static operandName => operandName, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateEvidence is not null)
        {
            throw new ArgumentException(
                $"Durable projection evidence cannot contain duplicate operand '{duplicateEvidence.Key}'.",
                nameof(operandEvidence));
        }

        if (status == OperationalMetricEvaluationStatus.Calculated)
        {
            if (value is null || reasonCode is not null || reasonOperandName is not null)
            {
                throw new ArgumentException(
                    "Calculated projections require a value and no failure reason.",
                    nameof(status));
            }
        }
        else if (value is not null || reasonCode is null)
        {
            throw new ArgumentException(
                "Non-calculated projections require a reason and no value.",
                nameof(status));
        }

        ProcessorId = processorId;
        Key = key;
        Status = status;
        Value = value;
        Unit = unit;
        ReasonCode = reasonCode;
        ReasonOperandName = reasonOperandName;
        SourceRevision = sourceRevision;
        OperandEvidence = new ReadOnlyCollection<OperationalMetricComponentProjectionEvidence>(operandSnapshot);
        DependencyEvidence = new ReadOnlyCollection<OperationalMetricDependencyProjectionEvidence>(dependencySnapshot);
        Evidence = new ReadOnlyCollection<OperationalMetricProjectionEvidence>(
            [.. operandSnapshot, .. dependencySnapshot]);
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public OperationalMetricEvaluationKey Key { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public IReadOnlyList<OperationalMetricComponentProjectionEvidence> OperandEvidence { get; }

    public IReadOnlyList<OperationalMetricDependencyProjectionEvidence> DependencyEvidence { get; }

    public IReadOnlyList<OperationalMetricProjectionEvidence> Evidence { get; }
}

public sealed record OperationalMetricProjectionCheckpoint
{
    public OperationalMetricProjectionCheckpoint(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(sourceRevision);

        ProcessorId = processorId;
        SourceRevision = sourceRevision;
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }
}

public sealed record OperationalMetricProjectionCommit
{
    public OperationalMetricProjectionCommit(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricProjectionCheckpoint? expectedCheckpoint,
        OperationalMetricProjectionCheckpoint proposedCheckpoint,
        IReadOnlyList<OperationalMetricProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(proposedCheckpoint);
        ArgumentNullException.ThrowIfNull(projections);

        if (proposedCheckpoint.ProcessorId != processorId)
        {
            throw new ArgumentException(
                "Proposed projection checkpoint must belong to the committing processor.",
                nameof(proposedCheckpoint));
        }

        if (expectedCheckpoint is not null)
        {
            if (expectedCheckpoint.ProcessorId != processorId ||
                expectedCheckpoint.SourceRevision.ProcessorId != proposedCheckpoint.SourceRevision.ProcessorId ||
                expectedCheckpoint.SourceRevision.StreamId != proposedCheckpoint.SourceRevision.StreamId)
            {
                throw new ArgumentException(
                    "Expected projection checkpoint must belong to the same projection processor and FC-026 source stream.",
                    nameof(expectedCheckpoint));
            }

            if (proposedCheckpoint.SourceRevision.Position <= expectedCheckpoint.SourceRevision.Position)
            {
                throw new ArgumentException(
                    "Proposed projection checkpoint must advance beyond the expected source revision.",
                    nameof(proposedCheckpoint));
            }
        }

        var snapshot = projections.ToArray();
        if (snapshot.Any(static projection => projection is null))
        {
            throw new ArgumentException(
                "Projection commits must not contain null projections.",
                nameof(projections));
        }

        if (snapshot.Any(projection =>
            projection.ProcessorId != processorId ||
            projection.SourceRevision != proposedCheckpoint.SourceRevision ||
            projection.Key.MachineId != proposedCheckpoint.SourceRevision.StreamId.MachineId))
        {
            throw new ArgumentException(
                "Every committed projection must belong to the committing processor, proposed FC-026 source revision, and source machine.",
                nameof(projections));
        }

        var duplicateKey = snapshot
            .GroupBy(projection => projection.Key)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null)
        {
            throw new ArgumentException(
                "Projection commits cannot contain duplicate evaluation keys.",
                nameof(projections));
        }

        ProcessorId = processorId;
        ExpectedCheckpoint = expectedCheckpoint;
        ProposedCheckpoint = proposedCheckpoint;
        Projections = new ReadOnlyCollection<OperationalMetricProjection>(snapshot);
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public OperationalMetricProjectionCheckpoint? ExpectedCheckpoint { get; }

    public OperationalMetricProjectionCheckpoint ProposedCheckpoint { get; }

    public IReadOnlyList<OperationalMetricProjection> Projections { get; }
}

public interface IOperationalMetricProjectionStore
{
    ValueTask<OperationalMetricProjectionCheckpoint?> ReadCheckpointAsync(
        OperationalMetricProjectionProcessorId processorId,
        MetricInputStreamId sourceStreamId,
        CancellationToken cancellationToken);

    ValueTask<OperationalMetricProjection?> ReadProjectionAsync(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        CancellationToken cancellationToken);

    ValueTask CommitAsync(
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken);
}
