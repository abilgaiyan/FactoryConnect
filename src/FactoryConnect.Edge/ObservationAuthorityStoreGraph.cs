using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

internal sealed class ObservationAuthorityStoreGraph
{
    public ObservationAuthorityStoreGraph(IServiceProvider provider)
    {
        var providerServices =
            provider.GetService<PersistenceProviderServices>();
        var mappingStore =
            providerServices?.MappingCoverageAuthorityStore;
        var stateActivityStore =
            providerServices?.MachineStateActivityAuthorityStore;

        if ((mappingStore is null) != (stateActivityStore is null))
        {
            throw new InvalidOperationException(
                "The selected persistence provider must supply mapping " +
                "coverage and machine state/activity authority stores " +
                "together.");
        }

        if (mappingStore is not null && stateActivityStore is not null)
        {
            MappingStore = mappingStore;
            StateActivityStore = stateActivityStore;
            return;
        }

        MappingStore = new InMemoryMappingCoverageAuthorityStore();
        StateActivityStore =
            new InMemoryMachineStateActivityAuthorityStore();
    }

    public IMappingCoverageAuthorityStore MappingStore { get; }

    public IMachineStateActivityAuthorityStore StateActivityStore { get; }
}
