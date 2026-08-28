using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

internal sealed record OperationalMetricComponentRequirement(
    string ComponentKey,
    MetricDimension RequiredDimension,
    string RequiredUnit);

internal sealed class OperationalMetricEvaluationPlan
{
    private readonly ReadOnlyDictionary<OperationalMetricDefinitionId, OperationalMetricDefinition> _definitionsById;

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

        var dependencySnapshot = dependencyOrder.ToArray();
        if (dependencySnapshot.Any(definition => definition is null))
        {
            throw new ArgumentException("Evaluation plans cannot contain null definitions.", nameof(dependencyOrder));
        }

        var rootCount = dependencySnapshot.Count(definition => definition.Id == rootDefinition.Id);
        if (rootDefinition.Id != rootKey.DefinitionId || rootCount != 1)
        {
            throw new ArgumentException(
                "Evaluation plan must contain its exact root definition exactly once.",
                nameof(dependencyOrder));
        }

        var duplicateDefinition = dependencySnapshot
            .GroupBy(definition => definition.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDefinition is not null)
        {
            throw new ArgumentException(
                $"Evaluation plan contains duplicate definition '{duplicateDefinition.Key.MetricKey}/{duplicateDefinition.Key.Version}'.",
                nameof(dependencyOrder));
        }

        var definitionsById = dependencySnapshot.ToDictionary(definition => definition.Id);

        RootKey = rootKey;
        RootDefinition = rootDefinition;
        DependencyOrder = new ReadOnlyCollection<OperationalMetricDefinition>(dependencySnapshot);
        ComponentRequirements = new ReadOnlyCollection<OperationalMetricComponentRequirement>(componentRequirements.ToArray());
        _definitionsById = new ReadOnlyDictionary<OperationalMetricDefinitionId, OperationalMetricDefinition>(definitionsById);
    }

    public OperationalMetricEvaluationKey RootKey { get; }

    public OperationalMetricDefinition RootDefinition { get; }

    public IReadOnlyList<OperationalMetricDefinition> DependencyOrder { get; }

    public IReadOnlyList<OperationalMetricComponentRequirement> ComponentRequirements { get; }

    public OperationalMetricDefinition GetRequiredDefinition(OperationalMetricDefinitionId definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        return _definitionsById.TryGetValue(definitionId, out var definition)
            ? definition
            : throw new InvalidOperationException(
                $"Metric '{definitionId.MetricKey}/{definitionId.Version}' is not part of this evaluation plan.");
    }
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
    private readonly HashSet<OperationalMetricDefinitionId> _plannedDefinitionIds;
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
        _plannedDefinitionIds = plan.DependencyOrder.Select(definition => definition.Id).ToHashSet();
    }

    public OperationalMetricEvaluationPlan Plan { get; }

    public OperationalMetricComponentSnapshot Snapshot { get; }

    public bool TryGetEvaluation(
        OperationalMetricDefinitionId definitionId,
        out OperationalMetricEvaluation? evaluation)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        EnsurePlanned(definitionId);
        return _completedEvaluations.TryGetValue(definitionId, out evaluation);
    }

    public void BeginEvaluation(OperationalMetricDefinitionId definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        EnsurePlanned(definitionId);

        if (_completedEvaluations.ContainsKey(definitionId))
        {
            throw new InvalidOperationException(
                $"Operational metric '{definitionId.MetricKey}/{definitionId.Version}' is already completed in this evaluation session.");
        }

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
        EnsurePlanned(definitionId);

        if (!_activeEvaluations.Contains(definitionId))
        {
            throw new InvalidOperationException("Metric evaluation must be active before completion.");
        }

        var expectedKey = new OperationalMetricEvaluationKey(
            Plan.RootKey.MachineId,
            Plan.RootKey.PeriodId,
            definitionId,
            Plan.RootKey.ContextKey);

        if (evaluation.Key != expectedKey || evaluation.SourceRevision != Snapshot.Revision)
        {
            throw new InvalidDataException(
                "Completed evaluation does not belong to the evaluation session identity and coherent source revision.");
        }

        if (!_completedEvaluations.TryAdd(definitionId, evaluation))
        {
            throw new InvalidOperationException(
                $"Operational metric '{definitionId.MetricKey}/{definitionId.Version}' is already completed in this evaluation session.");
        }

        _activeEvaluations.Remove(definitionId);
    }

    public void AbandonEvaluation(OperationalMetricDefinitionId definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        EnsurePlanned(definitionId);
        _activeEvaluations.Remove(definitionId);
    }

    private void EnsurePlanned(OperationalMetricDefinitionId definitionId)
    {
        if (!_plannedDefinitionIds.Contains(definitionId))
        {
            throw new InvalidOperationException(
                $"Metric '{definitionId.MetricKey}/{definitionId.Version}' is not part of this evaluation plan.");
        }
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
