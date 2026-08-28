using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public static class OperationalMetricDefinitionValidator
{
    public static void Validate(OperationalMetricDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Id);
        ArgumentNullException.ThrowIfNull(definition.SupportedScopes);
        ArgumentNullException.ThrowIfNull(definition.Operands);
        ArgumentNullException.ThrowIfNull(definition.Formula);
        ArgumentNullException.ThrowIfNull(definition.DomainConstraints);
        ArgumentNullException.ThrowIfNull(definition.PrecisionPolicy);

        if (!definition.SupportedScopes.SupportsShift && !definition.SupportedScopes.SupportsProductionDay)
        {
            throw new ArgumentException("At least one evaluation scope is required.", nameof(definition));
        }

        if (definition.Operands.Count == 0)
        {
            throw new ArgumentException("At least one operand is required.", nameof(definition));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ResultUnit);

        ValidatePrecision(definition.PrecisionPolicy);
        ValidateDomain(definition.DomainConstraints);

        var operands = new Dictionary<string, OperationalMetricOperandDefinition>(StringComparer.Ordinal);
        foreach (var operand in definition.Operands)
        {
            ArgumentNullException.ThrowIfNull(operand);
            ArgumentException.ThrowIfNullOrWhiteSpace(operand.OperandName);
            if (!string.Equals(operand.OperandName, operand.OperandName.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Operand names must not contain leading or trailing whitespace.",
                    nameof(definition));
            }

            ArgumentNullException.ThrowIfNull(operand.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(operand.RequiredUnit);

            if (!operands.TryAdd(operand.OperandName, operand))
            {
                throw new ArgumentException($"Duplicate operand '{operand.OperandName}'.", nameof(definition));
            }

            ValidateSource(operand.Source);
            ValidateDimensionUnit(operand.RequiredDimension, operand.RequiredUnit);
        }

        ValidateFormula(definition, operands);
    }

    private static void ValidateSource(OperationalMetricOperandSource source)
    {
        switch (source)
        {
            case OperationalMetricOperandSource.Component component:
                ArgumentException.ThrowIfNullOrWhiteSpace(component.ComponentKey);
                break;
            case OperationalMetricOperandSource.EvaluatedMetric evaluated:
                ArgumentNullException.ThrowIfNull(evaluated.DefinitionId);
                break;
            default:
                throw new ArgumentException("Unsupported operational metric operand source.", nameof(source));
        }
    }

    private static void ValidateDimensionUnit(MetricDimension dimension, string unit)
    {
        var valid = dimension switch
        {
            MetricDimension.Duration => string.Equals(unit, MetricInputFactUnits.Seconds, StringComparison.Ordinal),
            MetricDimension.Quantity => string.Equals(unit, MetricInputFactUnits.Count, StringComparison.Ordinal),
            MetricDimension.Ratio => string.Equals(unit, OperationalMetricUnits.Ratio, StringComparison.Ordinal),
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException($"Unit '{unit}' is not valid for dimension '{dimension}'.", nameof(unit));
        }
    }

    private static void ValidateFormula(
        OperationalMetricDefinition definition,
        IReadOnlyDictionary<string, OperationalMetricOperandDefinition> operands)
    {
        HashSet<string> referenced;

        switch (definition.Formula)
        {
            case OperationalMetricFormula.Ratio ratio:
                ArgumentException.ThrowIfNullOrWhiteSpace(ratio.NumeratorOperand);
                ArgumentException.ThrowIfNullOrWhiteSpace(ratio.DenominatorOperand);

                if (string.Equals(ratio.NumeratorOperand, ratio.DenominatorOperand, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Ratio numerator and denominator must be distinct operands.", nameof(definition));
                }

                EnsureDeclared(operands, ratio.NumeratorOperand);
                EnsureDeclared(operands, ratio.DenominatorOperand);

                var numerator = operands[ratio.NumeratorOperand];
                var denominator = operands[ratio.DenominatorOperand];
                if (numerator.RequiredDimension != denominator.RequiredDimension)
                {
                    throw new ArgumentException("Ratio operands must have compatible dimensions.", nameof(definition));
                }

                if (!string.Equals(definition.ResultUnit, OperationalMetricUnits.Ratio, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Ratio formulas must produce the canonical ratio unit.", nameof(definition));
                }

                referenced = new HashSet<string>(StringComparer.Ordinal)
                {
                    ratio.NumeratorOperand,
                    ratio.DenominatorOperand,
                };
                break;

            case OperationalMetricFormula.Product product:
                ArgumentNullException.ThrowIfNull(product.FactorOperands);
                if (product.FactorOperands.Count < 2)
                {
                    throw new ArgumentException("Product formulas require at least two factors.", nameof(definition));
                }

                referenced = new HashSet<string>(StringComparer.Ordinal);
                foreach (var factor in product.FactorOperands)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(factor);
                    EnsureDeclared(operands, factor);
                    if (!referenced.Add(factor))
                    {
                        throw new ArgumentException($"Duplicate product factor '{factor}'.", nameof(definition));
                    }

                    var operand = operands[factor];
                    if (operand.RequiredDimension != MetricDimension.Ratio ||
                        !string.Equals(operand.RequiredUnit, OperationalMetricUnits.Ratio, StringComparison.Ordinal) ||
                        operand.Source is not OperationalMetricOperandSource.EvaluatedMetric)
                    {
                        throw new ArgumentException(
                            "FC-027 product formulas may compose only ratio-valued evaluated metrics.",
                            nameof(definition));
                    }
                }

                if (!string.Equals(definition.ResultUnit, OperationalMetricUnits.Ratio, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Product formulas must produce the canonical ratio unit.", nameof(definition));
                }

                break;

            default:
                throw new ArgumentException("Unsupported operational metric formula type.", nameof(definition));
        }

        if (referenced.Count != operands.Count || operands.Keys.Any(key => !referenced.Contains(key)))
        {
            throw new ArgumentException("Every declared operand must be used exactly by the formula.", nameof(definition));
        }
    }

    private static void EnsureDeclared(
        IReadOnlyDictionary<string, OperationalMetricOperandDefinition> operands,
        string operandName)
    {
        if (!operands.ContainsKey(operandName))
        {
            throw new ArgumentException($"Formula references undeclared operand '{operandName}'.", nameof(operandName));
        }
    }

    private static void ValidateDomain(OperationalMetricDomainConstraints constraints)
    {
        if (constraints.MinimumInclusive is not null &&
            constraints.MaximumInclusive is not null &&
            constraints.MinimumInclusive.Value > constraints.MaximumInclusive.Value)
        {
            throw new ArgumentException("Metric domain minimum cannot exceed maximum.", nameof(constraints));
        }
    }

    private static void ValidatePrecision(OperationalMetricPrecisionPolicy precisionPolicy)
    {
        if (precisionPolicy.DurableDecimalScale is < 0 or > 28)
        {
            throw new ArgumentOutOfRangeException(
                nameof(precisionPolicy),
                "Durable decimal scale must be between 0 and 28.");
        }

        if (!Enum.IsDefined(precisionPolicy.RoundingMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(precisionPolicy),
                "Rounding mode must be a defined MidpointRounding value.");
        }
    }
}

public static class OperationalMetricDefinitionSemanticComparer
{
    public static bool AreEquivalent(OperationalMetricDefinition left, OperationalMetricDefinition right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.Id == right.Id &&
            left.SupportedScopes == right.SupportedScopes &&
            string.Equals(left.ResultUnit, right.ResultUnit, StringComparison.Ordinal) &&
            FormulasAreEquivalent(left.Formula, right.Formula) &&
            left.DomainConstraints == right.DomainConstraints &&
            left.PrecisionPolicy == right.PrecisionPolicy &&
            OperandsAreEquivalent(left.Operands, right.Operands);
    }

    private static bool FormulasAreEquivalent(
        OperationalMetricFormula left,
        OperationalMetricFormula right) =>
        (left, right) switch
        {
            (OperationalMetricFormula.Ratio leftRatio, OperationalMetricFormula.Ratio rightRatio) =>
                StringComparer.Ordinal.Equals(leftRatio.NumeratorOperand, rightRatio.NumeratorOperand) &&
                StringComparer.Ordinal.Equals(leftRatio.DenominatorOperand, rightRatio.DenominatorOperand),
            (OperationalMetricFormula.Product leftProduct, OperationalMetricFormula.Product rightProduct) =>
                leftProduct.FactorOperands.SequenceEqual(rightProduct.FactorOperands, StringComparer.Ordinal),
            _ => false,
        };

    private static bool OperandsAreEquivalent(
        IReadOnlyList<OperationalMetricOperandDefinition> left,
        IReadOnlyList<OperationalMetricOperandDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftByName = left.OrderBy(operand => operand.OperandName, StringComparer.Ordinal);
        var rightByName = right.OrderBy(operand => operand.OperandName, StringComparer.Ordinal);
        return leftByName.SequenceEqual(rightByName);
    }
}

public sealed class OperationalMetricDefinitionCatalog : IOperationalMetricDefinitionCatalog
{
    private readonly Dictionary<OperationalMetricDefinitionId, OperationalMetricDefinition> _definitions;
    private readonly Dictionary<OperationalMetricEvaluationScope, IReadOnlyList<OperationalMetricDefinition>> _orders;

    public OperationalMetricDefinitionCatalog(IEnumerable<OperationalMetricDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var byId = new Dictionary<OperationalMetricDefinitionId, OperationalMetricDefinition>();

        foreach (var incomingDefinition in definitions)
        {
            ArgumentNullException.ThrowIfNull(incomingDefinition);
            var definition = Snapshot(incomingDefinition);
            OperationalMetricDefinitionValidator.Validate(definition);

            if (byId.TryGetValue(definition.Id, out var existing))
            {
                if (!OperationalMetricDefinitionSemanticComparer.AreEquivalent(existing, definition))
                {
                    throw new ArgumentException(
                        $"Conflicting operational metric definition '{definition.Id.MetricKey}/{definition.Id.Version}'.",
                        nameof(definitions));
                }

                continue;
            }

            byId.Add(definition.Id, definition);
        }

        ValidateDependencies(byId);
        _definitions = byId;
        _orders = new Dictionary<OperationalMetricEvaluationScope, IReadOnlyList<OperationalMetricDefinition>>
        {
            [OperationalMetricEvaluationScope.Shift] = new ReadOnlyCollection<OperationalMetricDefinition>(
                BuildOrder(byId, OperationalMetricEvaluationScope.Shift)),
            [OperationalMetricEvaluationScope.ProductionDay] = new ReadOnlyCollection<OperationalMetricDefinition>(
                BuildOrder(byId, OperationalMetricEvaluationScope.ProductionDay)),
        };
    }

    public OperationalMetricDefinition GetRequired(OperationalMetricDefinitionId definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        return _definitions.TryGetValue(definitionId, out var definition)
            ? definition
            : throw new KeyNotFoundException(
                $"Operational metric definition '{definitionId.MetricKey}/{definitionId.Version}' was not registered.");
    }

    public IReadOnlyList<OperationalMetricDefinition> GetEvaluationOrder(OperationalMetricEvaluationScope scope) =>
        _orders.TryGetValue(scope, out var order)
            ? order
            : throw new ArgumentOutOfRangeException(nameof(scope));

    private static OperationalMetricDefinition Snapshot(OperationalMetricDefinition source)
    {
        var operands = source.Operands
            .Select(operand => operand with { })
            .ToArray();
        var readOnlyOperands = new ReadOnlyCollection<OperationalMetricOperandDefinition>(operands);

        OperationalMetricFormula formula = source.Formula switch
        {
            OperationalMetricFormula.Ratio ratio =>
                new OperationalMetricFormula.Ratio(ratio.NumeratorOperand, ratio.DenominatorOperand),
            OperationalMetricFormula.Product product =>
                new OperationalMetricFormula.Product(
                    new ReadOnlyCollection<string>(product.FactorOperands.ToArray())),
            _ => source.Formula,
        };

        return new OperationalMetricDefinition
        {
            Id = new OperationalMetricDefinitionId(source.Id.MetricKey, source.Id.Version),
            DisplayName = source.DisplayName,
            SupportedScopes = source.SupportedScopes with { },
            Operands = readOnlyOperands,
            ResultUnit = source.ResultUnit,
            Formula = formula,
            DomainConstraints = source.DomainConstraints with { },
            PrecisionPolicy = source.PrecisionPolicy with { },
        };
    }

    private static void ValidateDependencies(
        Dictionary<OperationalMetricDefinitionId, OperationalMetricDefinition> definitions)
    {
        foreach (var definition in definitions.Values)
        {
            foreach (var dependencyId in GetDependencies(definition))
            {
                if (!definitions.TryGetValue(dependencyId, out var dependency))
                {
                    throw new ArgumentException(
                        $"Metric '{definition.Id.MetricKey}/{definition.Id.Version}' depends on missing definition '{dependencyId.MetricKey}/{dependencyId.Version}'.",
                        nameof(definitions));
                }

                if (definition.SupportedScopes.SupportsShift && !dependency.SupportedScopes.SupportsShift ||
                    definition.SupportedScopes.SupportsProductionDay && !dependency.SupportedScopes.SupportsProductionDay)
                {
                    throw new ArgumentException(
                        $"Metric '{definition.Id.MetricKey}/{definition.Id.Version}' has a dependency that does not support all parent scopes.",
                        nameof(definitions));
                }
            }
        }

        _ = BuildOrder(definitions, OperationalMetricEvaluationScope.Shift);
        _ = BuildOrder(definitions, OperationalMetricEvaluationScope.ProductionDay);
    }

    private static List<OperationalMetricDefinition> BuildOrder(
        Dictionary<OperationalMetricDefinitionId, OperationalMetricDefinition> definitions,
        OperationalMetricEvaluationScope scope)
    {
        var applicable = definitions.Values
            .Where(definition => definition.SupportedScopes.Supports(scope))
            .ToDictionary(definition => definition.Id);

        var indegree = applicable.Keys.ToDictionary(id => id, _ => 0);
        var dependents = applicable.Keys.ToDictionary(id => id, _ => new List<OperationalMetricDefinitionId>());

        foreach (var definition in applicable.Values)
        {
            foreach (var dependency in GetDependencies(definition))
            {
                if (!applicable.ContainsKey(dependency))
                {
                    continue;
                }

                indegree[definition.Id]++;
                dependents[dependency].Add(definition.Id);
            }
        }

        var comparer = Comparer<OperationalMetricDefinitionId>.Create(CompareDefinitionIds);
        var ready = new SortedSet<OperationalMetricDefinitionId>(comparer);
        foreach (var pair in indegree)
        {
            if (pair.Value == 0)
            {
                ready.Add(pair.Key);
            }
        }

        var order = new List<OperationalMetricDefinition>(applicable.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            order.Add(applicable[next]);

            foreach (var dependent in dependents[next])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (order.Count != applicable.Count)
        {
            throw new ArgumentException("Operational metric definition dependency graph contains a cycle.", nameof(definitions));
        }

        return order;
    }

    private static IEnumerable<OperationalMetricDefinitionId> GetDependencies(OperationalMetricDefinition definition) =>
        definition.Operands
            .Select(operand => operand.Source)
            .OfType<OperationalMetricOperandSource.EvaluatedMetric>()
            .Select(source => source.DefinitionId);

    private static int CompareDefinitionIds(
        OperationalMetricDefinitionId? left,
        OperationalMetricDefinitionId? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var key = StringComparer.Ordinal.Compare(left.MetricKey, right.MetricKey);
        return key != 0 ? key : StringComparer.Ordinal.Compare(left.Version, right.Version);
    }
}
