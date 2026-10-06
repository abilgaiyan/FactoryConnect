using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

/// <summary>A complete, already evaluated historical Production Day. Does not authorize reevaluation or overwrite.</summary>
public sealed record OperationalMetricProjectionRecoveryRequest
{
    public OperationalMetricProjectionRecoveryRequest(OperationalMetricProjectionProcessorId processorId,
        ProductionDayId productionDayId, MetricAggregationCheckpoint sourceRevision,
        IReadOnlyList<OperationalMetricProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(productionDayId);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentNullException.ThrowIfNull(projections);
        var snapshot = projections.ToArray();
        var period = new OperationalMetricPeriodId.ProductionDay(productionDayId);
        string[] metrics = ["availability", "performance", "quality", "oee", "utilization.elr"];
        if (snapshot.Length != 5 || snapshot.Any(p => p is null || p.ProcessorId != processorId ||
            p.SourceRevision != sourceRevision || p.Key.PeriodId != period ||
            p.Key.ContextKey != OperationalMetricEvaluationContextKey.Unpartitioned ||
            p.Key.DefinitionId.Version != "1.0") ||
            !snapshot.Select(p => p.Key.DefinitionId.MetricKey).ToHashSet(StringComparer.Ordinal).SetEquals(metrics))
            throw new ArgumentException("Recovery requires the exact five v1.0 unpartitioned Production Day projections at one historical revision.", nameof(projections));
        foreach (var projection in snapshot)
        {
            foreach (var dependency in projection.DependencyEvidence)
            {
                if (!snapshot.Any(p => p.Key == dependency.Projection.Key))
                    throw new ArgumentException("Recovery dependencies must belong to the complete expected set.", nameof(projections));
            }
        }
        ProcessorId = processorId;
        ProductionDayId = productionDayId;
        SourceRevision = sourceRevision;
        Projections = new ReadOnlyCollection<OperationalMetricProjection>(snapshot);
    }
    public OperationalMetricProjectionProcessorId ProcessorId { get; }
    public ProductionDayId ProductionDayId { get; }
    public MetricAggregationCheckpoint SourceRevision { get; }
    public IReadOnlyList<OperationalMetricProjection> Projections { get; }
}

public enum OperationalMetricProjectionRecoveryOutcome { Recovered, Equivalent, Conflict }
public sealed record OperationalMetricProjectionRecoveryResult(OperationalMetricProjectionRecoveryOutcome Outcome,
    int InsertedProjectionCount);

public interface IOperationalMetricProjectionRecoveryStore
{
    ValueTask<OperationalMetricProjectionRecoveryResult> RecoverAsync(
        OperationalMetricProjectionRecoveryRequest request, CancellationToken cancellationToken);
}
