using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

internal sealed record OperationalMetricComponentRequirement(
    string ComponentKey,
    MetricDimension RequiredDimension,
    string RequiredUnit);

internal sealed class OperationalMetricEvaluationPlan
{
    public OperationalMetricEvaluationPlan(
        OperationalMetricEvaluationKey rootKey,
        OperationalMetricDefinition rootDefinition,
        IEnumerable<OperationalMetricDefinition> dependencyOrder,
        IEnumerable<OperationalMetricComponentRequirement> componentRequirements)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(rootDefinition);
        ArgumentNullException.ThrowIfNull(dependencyOrder);
        ArgumentNullException.ThrowIfNull(componentRequirements);

        RootKey = rootKey;
        RootDefinition = rootDefinition;
        DependencyOrder = new ReadOnlyCollection<OperationalMetricDefinition>(dependencyOrder.ToArray());
        ComponentRequirements = new ReadOnlyCollection<OperationalMetricComponentRequirement>(componentRequirements.ToArray());
    }

    public OperationalMetricEvaluationKey RootKey { get; }

    public OperationalMetricDefinition RootDefinition { get; }

    public IReadOnlyList<OperationalMetricDefinition> DependencyOrder { get; }

    public IReadOnlyList<OperationalMetricComponentRequirement> ComponentRequirements { get; }
}

internal sealed class OperationalMetricEvaluationPlanner
{
    private readonly IOperationalMetricDefinitionCatalog _catalog;

    public OperationalMetricEvaluationPlanner(IOperationalMetricDefinitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public OperationalMetricEvaluationPlan CreatePlan(OperationalMetricEvaluationKey rootKey)
    {
        ArgumentNullException.ThrowIfNull(rootKey);

        var root = _catalog.GetRequired(rootKey.DefinitionId);
        if (!root.SupportedScopes.Supports(rootKey.Scope))
        {
            throw new InvalidOperationException(
                $"Metric definition '{root.Id}' does not support evaluation scope '{rootKey.Scope}'.");
        }

        var active = new HashSet<OperationalMetricDefinitionId>();
        var completed = new HashSet<OperationalMetricDefinitionId>();
        var ordered = new List<OperationalMetricDefinition>();
        var requirements = new Dictionary<string, OperationalMetricComponentRequirement>(StringComparer.Ordinal);

        Visit(root, rootKey.Scope, active, completed, ordered, requirements);

        var canonicalRequirements = requirements.Values
            .OrderBy(requirement => requirement.ComponentKey, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.RequiredDimension)
            .ThenBy(requirement => requirement.RequiredUnit, StringComparer.Ordinal)
            .ToArray();

        return new OperationalMetricEvaluationPlan(rootKey, root, ordered, canonicalRequirements);
    }

    private void Visit(
        OperationalMetricDefinition definition,
        OperationalMetricEvaluationScope scope,
        HashSet<OperationalMetricDefinitionId> active,
        HashSet<OperationalMetricDefinitionId> completed,
        List<OperationalMetricDefinition> ordered,
        Dictionary<string, OperationalMetricComponentRequirement> requirements)
    {
        if (completed.Contains(definition.Id))
        {
            return;
        }

        if (!active.Add(definition.Id))
        {
            throw new InvalidDataException(
                $"Operational metric dependency cycle detected at '{definition.Id.MetricKey}/{definition.Id.Version}'.");
        }

        foreach (var dependency in GetDependenciesInAuthoredOrder(definition))
        {
            if (!dependency.SupportedScopes.Supports(scope))
            {
                throw new InvalidDataException(
                    $"Metric dependency '{dependency.Id}' does not support root evaluation scope '{scope}'.");
            }

            Visit(dependency, scope, active, completed, ordered, requirements);
        }

        foreach (var operand in definition.Operands)
        {
            if (operand.Source is not OperationalMetricOperandSource.Component component)
            {
                continue;
            }

            var requirement = new OperationalMetricComponentRequirement(
                component.ComponentKey,
                operand.RequiredDimension,
                operand.RequiredUnit);

            if (requirements.TryGetValue(component.ComponentKey, out var existing))
            {
                if (existing != requirement)
                {
                    throw new InvalidDataException(
                        $"Component '{component.ComponentKey}' has incompatible transitive requirements.");
                }

                continue;
            }

            requirements.Add(component.ComponentKey, requirement);
        }

        active.Remove(definition.Id);
        completed.Add(definition.Id);
        ordered.Add(definition);
    }

    private IEnumerable<OperationalMetricDefinition> GetDependenciesInAuthoredOrder(
        OperationalMetricDefinition definition)
    {
        if (definition.Formula is OperationalMetricFormula.Product product)
        {
            var byName = definition.Operands.ToDictionary(operand => operand.OperandName, StringComparer.Ordinal);
            foreach (var factorName in product.FactorOperands)
            {
                var operand = byName[factorName];
                var source = operand.Source as OperationalMetricOperandSource.EvaluatedMetric
                    ?? throw new InvalidDataException(
                        $"Product factor '{factorName}' must reference an evaluated metric.");
                yield return _catalog.GetRequired(source.DefinitionId);
            }

            yield break;
        }

        foreach (var operand in definition.Operands)
        {
            if (operand.Source is OperationalMetricOperandSource.EvaluatedMetric evaluated)
            {
                yield return _catalog.GetRequired(evaluated.DefinitionId);
            }
        }
    }
}

internal sealed class OperationalMetricEvaluationSession
{
    private readonly Dictionary<OperationalMetricDefinitionId, OperationalMetricEvaluation> _completedEvaluations = new();
    private readonly HashSet<OperationalMetricDefinitionId> _activeEvaluations = [];

    public OperationalMetricEvaluationSession(
        OperationalMetricEvaluationPlan plan,
        OperationalMetricComponentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.EvaluationKey != plan.RootKey)
        {
            throw new ArgumentException("Session snapshot must belong to the root evaluation key.", nameof(snapshot));
        }

        Plan = plan;
        Snapshot = snapshot;
    }

    public OperationalMetricEvaluationPlan Plan { get; }

    public OperationalMetricComponentSnapshot Snapshot { get; }

    public bool TryGetEvaluation(
        OperationalMetricDefinitionId definitionId,
        out OperationalMetricEvaluation? evaluation)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        return _completedEvaluations.TryGetValue(definitionId, out evaluation);
    }

    public void BeginEvaluation(OperationalMetricDefinitionId definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        if (!_activeEvaluations.Add(definitionId))
        {
            throw new InvalidDataException(
                $"Operational metric evaluation cycle detected at '{definitionId.MetricKey}/{definitionId.Version}'.");
        }
    }

    public void CompleteEvaluation(
        OperationalMetricDefinitionId definitionId,
        OperationalMetricEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!_activeEvaluations.Remove(definitionId))
        {
            throw new InvalidOperationException("Metric evaluation must be active before completion.");
        }

        if (evaluation.Key.DefinitionId != definitionId)
        {
            throw new ArgumentException(
                "Completed evaluation must match the exact active definition ID.",
                nameof(evaluation));
        }

        _completedEvaluations[definitionId] = evaluation;
    }

    public void AbandonEvaluation(OperationalMetricDefinitionId definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        _activeEvaluations.Remove(definitionId);
    }
}

internal sealed class OperationalMetricEvaluationSessionFactory
{
    private readonly IOperationalMetricComponentSnapshotReader _snapshotReader;
    private readonly MetricAggregationProcessorId _aggregationProcessorId;

    public OperationalMetricEvaluationSessionFactory(
        IOperationalMetricComponentSnapshotReader snapshotReader,
        MetricAggregationProcessorId aggregationProcessorId)
    {
        ArgumentNullException.ThrowIfNull(snapshotReader);
        ArgumentNullException.ThrowIfNull(aggregationProcessorId);
        _snapshotReader = snapshotReader;
        _aggregationProcessorId = aggregationProcessorId;
    }

    public async ValueTask<OperationalMetricEvaluationSession> CreateAsync(
        OperationalMetricEvaluationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var operands = plan.ComponentRequirements
            .Select(requirement => new OperationalMetricOperandDefinition
            {
                OperandName = requirement.ComponentKey,
                Source = new OperationalMetricOperandSource.Component(requirement.ComponentKey),
                RequiredDimension = requirement.RequiredDimension,
                RequiredUnit = requirement.RequiredUnit,
            })
            .ToArray();

        var snapshot = await _snapshotReader.ReadAsync(
            new OperationalMetricComponentSnapshotRequest(plan.RootKey, _aggregationProcessorId, operands),
            cancellationToken).ConfigureAwait(false);

        if (snapshot.EvaluationKey != plan.RootKey ||
            snapshot.Revision.ProcessorId != _aggregationProcessorId)
        {
            throw new InvalidDataException(
                "Operational metric evaluation session snapshot does not match the requested root identity and aggregation processor.");
        }

        return new OperationalMetricEvaluationSession(plan, snapshot);
    }
}
