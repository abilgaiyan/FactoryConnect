using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

internal sealed class ProductionActivityAssociation
{
    private readonly IServiceProvider _provider;
    private readonly Lazy<InMemoryMachineStateActivityAuthorityStore> _stateActivityStore;
    private readonly Lazy<JointProductionContextActivityReader> _activityReader;

    public ProductionActivityAssociation(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _provider = provider;
        _stateActivityStore = new Lazy<InMemoryMachineStateActivityAuthorityStore>(
            ResolveStateActivityStore);
        _activityReader = new Lazy<JointProductionContextActivityReader>(
            () => new JointProductionContextActivityReader(
                _stateActivityStore.Value,
                new ObservationProcessorId("machine-state-activity")));
    }

    public InMemoryMachineStateActivityAuthorityStore StateActivityStore =>
        _stateActivityStore.Value;

    public JointProductionContextActivityReader ActivityReader =>
        _activityReader.Value;

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
