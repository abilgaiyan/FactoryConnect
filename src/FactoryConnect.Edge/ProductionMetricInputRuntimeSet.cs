using FactoryConnect.Core;

namespace FactoryConnect.Edge;

public sealed class ProductionMetricInputRuntimeSet
{
    private readonly IReadOnlyList<ProductionContextProcessingRuntime> _activityRuntimes;
    private readonly IReadOnlyList<ProductionQuantityFactProcessingRuntime> _quantityRuntimes;

    public ProductionMetricInputRuntimeSet(
        IReadOnlyList<ProductionContextProcessingRuntime> activityRuntimes,
        IReadOnlyList<ProductionQuantityFactProcessingRuntime> quantityRuntimes,
        TimeSpan pollingInterval)
    {
        ArgumentNullException.ThrowIfNull(activityRuntimes);
        ArgumentNullException.ThrowIfNull(quantityRuntimes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            pollingInterval,
            TimeSpan.Zero);

        _activityRuntimes = Array.AsReadOnly(activityRuntimes.ToArray());
        _quantityRuntimes = Array.AsReadOnly(quantityRuntimes.ToArray());
        PollingInterval = pollingInterval;
    }

    public IReadOnlyList<ProductionContextProcessingRuntime> ActivityRuntimes =>
        _activityRuntimes;

    public IReadOnlyList<ProductionQuantityFactProcessingRuntime> QuantityRuntimes =>
        _quantityRuntimes;

    public TimeSpan PollingInterval { get; }
}
