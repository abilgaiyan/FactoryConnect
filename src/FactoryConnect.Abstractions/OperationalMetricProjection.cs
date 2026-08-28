namespace FactoryConnect.Abstractions;

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
        MetricAggregationCheckpoint sourceRevision)
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
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public OperationalMetricEvaluationKey Key { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }
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
        Projections = snapshot;
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
