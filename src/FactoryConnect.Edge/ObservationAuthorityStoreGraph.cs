using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

internal sealed class ObservationAuthorityStoreGraph
{
    private static readonly ObservationProcessorId StateActivityProcessorId =
        new("machine-state-activity");

    public ObservationAuthorityStoreGraph(IServiceProvider provider)
    {
        var providerServices =
            provider.GetService<PersistenceProviderServices>();
        var mappingStore =
            providerServices?.MappingCoverageAuthorityStore;
        var stateActivityStore =
            providerServices?.MachineStateActivityAuthorityStore;
        var productionActivityReader =
            providerServices?.ProductionContextActivityReader;

        if ((mappingStore is null) != (stateActivityStore is null))
        {
            throw new InvalidOperationException(
                "The selected persistence provider must supply mapping " +
                "coverage and machine state/activity authority stores " +
                "together.");
        }

        if (stateActivityStore is not null)
        {
            if (productionActivityReader is null)
            {
                throw new InvalidOperationException(
                    "The selected persistence provider supplies machine " +
                    "state/activity authority without the matching production " +
                    "activity reader.");
            }

            MappingStore = mappingStore!;
            StateActivityStore = stateActivityStore;
            ProductionActivityReader = productionActivityReader;
            return;
        }

        if (productionActivityReader is not null)
        {
            throw new InvalidOperationException(
                "The selected persistence provider supplies a production " +
                "activity reader without the matching machine state/activity " +
                "authority graph.");
        }

        MappingStore = new InMemoryMappingCoverageAuthorityStore();
        var fallbackStateActivityStore =
            new InMemoryMachineStateActivityAuthorityStore();
        StateActivityStore = fallbackStateActivityStore;
        ProductionActivityReader = new JointProductionContextActivityReader(
            fallbackStateActivityStore,
            StateActivityProcessorId);
    }

    public IMappingCoverageAuthorityStore MappingStore { get; }

    public IMachineStateActivityAuthorityStore StateActivityStore { get; }

    public IProductionContextActivityReader ProductionActivityReader { get; }
}
