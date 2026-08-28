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

        var visited = new HashSet<OperationalMetricDefinitionId>();
        var ordered = new List<OperationalMetricDefinition>();
        var requirements = new Dictionary<string, OperationalMetricComponentRequirement>(StringComparer.Ordinal);

        Visit(root, rootKey.Scope, visited, ordered, requirements);

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
        HashSet<OperationalMetricDefinitionId> visited,
        List<OperationalMetricDefinition> ordered,
        Dictionary<string, OperationalMetricComponentRequirement> requirements)
    {
        if (!visited.Add(definition.Id))
        {
            return;
        }

        var dependencies = definition.Operands
            .Select(operand => operand.Source)
            .OfType<OperationalMetricOperandSource.EvaluatedMetric>()
            .Select(source => _catalog.GetRequired(source.DefinitionId))
            .OrderBy(dependency => dependency.Id.MetricKey, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Id.Version, StringComparer.Ordinal);

        foreach (var dependency in dependencies)
        {
            if (!dependency.SupportedScopes.Supports(scope))
            {
                throw new InvalidDataException(
                    $"Metric dependency '{dependency.Id}' does not support root evaluation scope '{scope}'.");
            }

            Visit(dependency, scope, visited, ordered, requirements);
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

        ordered.Add(definition);
    }
}

internal sealed class OperationalMetricEvaluationSession
{
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

        return new OperationalMetricEvaluationSession(plan, snapshot);
    }
}
