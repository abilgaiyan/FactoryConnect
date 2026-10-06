using FactoryConnect.Abstractions;

namespace FactoryConnect.HistoricalRecovery;

/// <summary>Serves only frozen historical inputs; never consults current SQL state.</summary>
public sealed class FrozenSnapshotReader(OperationalMetricComponentSnapshot snapshot)
    : IOperationalMetricComponentSnapshotReader
{
    public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
        OperationalMetricComponentSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProcessorId != snapshot.Revision.ProcessorId ||
            request.EvaluationKey.MachineId != snapshot.EvaluationKey.MachineId ||
            request.EvaluationKey.PeriodId != snapshot.EvaluationKey.PeriodId ||
            request.EvaluationKey.ContextKey != snapshot.EvaluationKey.ContextKey)
        {
            throw new InvalidDataException("Pinned snapshot request changed historical target identity.");
        }

        var requested = request.Operands.ToDictionary(
            operand => ((OperationalMetricOperandSource.Component)operand.Source).ComponentKey,
            StringComparer.Ordinal);
        var selected = snapshot.Components.Where(component => requested.ContainsKey(
            OperationalMetricComponentKeyMapping.ComponentKey(component.SourceIdentity.ComponentKey))).ToArray();
        foreach (var component in selected)
        {
            var requirement = requested[OperationalMetricComponentKeyMapping.ComponentKey(
                component.SourceIdentity.ComponentKey)];
            if (component.Dimension != requirement.RequiredDimension ||
                !string.Equals(component.Aggregate.Unit, requirement.RequiredUnit, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Pinned component does not match requested domain/unit.");
            }
        }

        return ValueTask.FromResult(new OperationalMetricComponentSnapshot(
            request.EvaluationKey, snapshot.Revision, selected));
    }
}
