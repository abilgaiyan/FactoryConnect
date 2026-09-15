using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

internal sealed class ProductionActivityAssociation
{
    private readonly IServiceProvider _provider;
    private readonly IServiceCollection _services;
    private readonly ServiceDescriptor _activityReaderDescriptor;
    private readonly Lazy<InMemoryMachineStateActivityAuthorityStore> _stateActivityStore;
    private readonly Lazy<JointProductionContextActivityReader> _activityReader;

    public ProductionActivityAssociation(
        IServiceProvider provider,
        IServiceCollection services,
        ServiceDescriptor activityReaderDescriptor)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(activityReaderDescriptor);

        _provider = provider;
        _services = services;
        _activityReaderDescriptor = activityReaderDescriptor;
        _stateActivityStore = new Lazy<InMemoryMachineStateActivityAuthorityStore>(
            ResolveStateActivityStore);
        _activityReader = new Lazy<JointProductionContextActivityReader>(
            CreateActivityReader);
    }

    public InMemoryMachineStateActivityAuthorityStore StateActivityStore =>
        _stateActivityStore.Value;

    public JointProductionContextActivityReader ActivityReader =>
        _activityReader.Value;

    private JointProductionContextActivityReader CreateActivityReader()
    {
        var effectiveDescriptor = _services.LastOrDefault(
            static descriptor =>
                descriptor.ServiceType == typeof(IProductionContextActivityReader));

        if (!ReferenceEquals(effectiveDescriptor, _activityReaderDescriptor))
        {
            throw new InvalidOperationException(
                "The FactoryConnect IProductionContextActivityReader registration " +
                "was displaced in the completed service graph.");
        }

        return new JointProductionContextActivityReader(
            _stateActivityStore.Value,
            new ObservationProcessorId("machine-state-activity"));
    }

    private InMemoryMachineStateActivityAuthorityStore ResolveStateActivityStore()
    {
        var observationGraph =
            _provider.GetService<ObservationAuthorityStoreGraph>();

        if (observationGraph is null)
        {
            return new InMemoryMachineStateActivityAuthorityStore();
        }

        return observationGraph.StateActivityStore as
            InMemoryMachineStateActivityAuthorityStore ??
            throw new InvalidOperationException(
                "The selected observation state/activity authority store does " +
                "not expose the activity-history capability required by " +
                "production processing.");
    }
}
