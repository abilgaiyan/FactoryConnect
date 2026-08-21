using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class MachineShiftMetricEvaluator
{
    private readonly MetricCalculationEngine _calculationEngine;

    public MachineShiftMetricEvaluator(
        MetricCalculationEngine calculationEngine)
    {
        ArgumentNullException.ThrowIfNull(calculationEngine);
        _calculationEngine = calculationEngine;
    }

    public MachineShiftMetricResult Evaluate(
        MachineShiftMetricEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var derived = MetricInputDeriver.Derive(
            new MetricInputDerivationRequest
            {
                CompanyId = request.CompanyId,
                SiteId = request.SiteId,
                MachineId = request.MachineId,
                ShiftId = request.ShiftId,
                ProductionDate = request.ProductionDate,
                ActivityPeriods = request.ActivityPeriods,
                Schedule = request.Schedule,
                ProductionEntries = request.ProductionEntries,
            });

        var normalizedInputs = new Dictionary<string, decimal>(
            derived.Inputs,
            StringComparer.OrdinalIgnoreCase);

        foreach (var input in request.AdditionalInputs)
        {
            if (!normalizedInputs.TryAdd(input.Key, input.Value))
            {
                throw new ArgumentException(
                    $"Additional metric input '{input.Key}' cannot replace a derived input.",
                    nameof(request));
            }
        }

        var evaluationInputs = new Dictionary<string, decimal>(
            normalizedInputs,
            StringComparer.OrdinalIgnoreCase);
        var metrics = new Dictionary<string, MetricCalculationResult>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var policy in request.MetricPolicies)
        {
            var result = _calculationEngine.Calculate(
                new MetricCalculationContext
                {
                    MetricKey = policy.MetricKey,
                    Inputs = evaluationInputs,
                },
                policy);

            if (!metrics.TryAdd(result.MetricKey, result))
            {
                throw new ArgumentException(
                    $"Metric policy '{result.MetricKey}' is duplicated.",
                    nameof(request));
            }

            if (result.IsAvailable && result.Value.HasValue)
            {
                evaluationInputs[result.MetricKey] = result.Value.Value;
            }
        }

        return new MachineShiftMetricResult
        {
            CompanyId = request.CompanyId,
            SiteId = request.SiteId,
            MachineId = request.MachineId,
            ShiftId = request.ShiftId,
            ProductionDate = request.ProductionDate,
            Inputs = normalizedInputs,
            Metrics = metrics,
        };
    }
}
