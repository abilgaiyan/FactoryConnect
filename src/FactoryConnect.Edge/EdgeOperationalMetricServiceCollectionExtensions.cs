using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgeOperationalMetricServiceCollectionExtensions
{
    public const string SectionName = "OperationalMetrics";

    public static IServiceCollection AddFactoryConnectOperationalMetrics(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<MachineId> machineIds)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(machineIds);

        if (machineIds.Count == 0)
        {
            throw new ArgumentException("At least one machine is required for operational metrics.", nameof(machineIds));
        }

        if (machineIds.Distinct().Count() != machineIds.Count)
        {
            throw new ArgumentException("Operational metric machines must be unique.", nameof(machineIds));
        }

        var machines = machineIds.ToArray();
        var section = configuration.GetRequiredSection(SectionName);
        var pollingInterval = TimeSpan.Parse(
            section["PollingInterval"] ??
                throw new InvalidOperationException("OperationalMetrics:PollingInterval is required."),
            CultureInfo.InvariantCulture);
        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("OperationalMetrics:PollingInterval must be greater than zero.");
        }

        var policy = section["EvaluationPolicyId"];
        if (policy is not null && policy != AvailabilityContainmentV1.PolicyId)
            throw new InvalidOperationException("OperationalMetrics:EvaluationPolicyId must be absent for legacy or the supported containment policy.");
        var containment = policy == AvailabilityContainmentV1.PolicyId;

        services.AddSingleton<IOperationalMetricDefinitionCatalog>(
            new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All));

        services.AddSingleton(
            provider =>
            {
                var catalog = provider.GetRequiredService<IOperationalMetricDefinitionCatalog>();
                var revisionReader = provider.GetRequiredService<IMetricAggregationRevisionReader>();
                var snapshotReader = provider.GetRequiredService<IRevisionedOperationalMetricComponentSnapshotReader>();
                var projectionStore = provider.GetRequiredService<IOperationalMetricProjectionStore>();
                var persistence = provider.GetService<PersistenceProviderServices>();
                var prerequisite = persistence?.OperationalMetricProjectionPrerequisite;
                var publisher = containment ? persistence?.OperationalMetricContainmentPublisher
                    ?? throw new InvalidOperationException("Selected persistence provider does not support containment publication.") : null;
                if (containment && prerequisite is null)
                    throw new InvalidOperationException("Containment composition requires reference-time convergence prerequisites.");

                var runtimes = machines
                    .Select(machineId =>
                    {
                        var sourceProcessorId = new MetricAggregationProcessorId(
                            $"metric-aggregation:{machineId.Value:D}");
                        var streamId = MetricInputStreamId.ForMachine(machineId);
                        var projectionProcessorId = containment ? AvailabilityContainmentV1.ProjectionProcessorId(machineId)
                            : new OperationalMetricProjectionProcessorId($"operational-metrics:{machineId.Value:D}:builtins-v1");
                        var source = new CoherentOperationalMetricEvaluationBatchSource(
                            catalog,
                            revisionReader,
                            snapshotReader,
                            sourceProcessorId,
                            streamId,
                            OperationalMetricEvaluationContextKey.Unpartitioned, containment);

                        return new OperationalMetricProjectionProcessingRuntime(
                            projectionProcessorId,
                            sourceProcessorId,
                            streamId,
                            source,
                            new OperationalMetricProjectionFactory(catalog, projectionProcessorId),
                            projectionStore,
                            prerequisite, publisher);
                    })
                    .ToArray();

                return new OperationalMetricProjectionProcessingRuntimeSet(runtimes, pollingInterval);
            });

        services.AddSingleton<IOperationalMetricReportReader>(
            provider => new OperationalMetricReportReader(
                provider.GetRequiredService<IOperationalMetricProjectionQueryReader>()));
        services.AddHostedService<OperationalMetricProjectionProcessingWorker>();
        return services;
    }
}
