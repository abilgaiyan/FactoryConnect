using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

internal sealed class ProductionActivityAssociation
{
    private static readonly ObservationProcessorId StateActivityProcessorId =
        new("machine-state-activity");

    private readonly IServiceProvider _provider;
    private readonly IServiceCollection _services;
    private readonly ServiceDescriptor _activityReaderDescriptor;
    private readonly Lazy<Association> _association;

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
        _association = new Lazy<Association>(
            CreateAssociation,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IMachineStateActivityAuthorityStore StateActivityStore =>
        _association.Value.StateActivityStore;

    public IProductionContextActivityReader ActivityReader
    {
        get
        {
            EnsureAuthoritativeReaderRegistration();
            return _association.Value.ActivityReader;
        }
    }

    private Association CreateAssociation()
    {
        var observationGraph =
            _provider.GetService<ObservationAuthorityStoreGraph>();

        if (observationGraph is not null)
        {
            return new Association(
                observationGraph.StateActivityStore,
                observationGraph.ProductionActivityReader);
        }

        var stateActivityStore =
            new InMemoryMachineStateActivityAuthorityStore();
        return new Association(
            stateActivityStore,
            new JointProductionContextActivityReader(
                stateActivityStore,
                StateActivityProcessorId));
    }

    private void EnsureAuthoritativeReaderRegistration()
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
    }

    private sealed record Association(
        IMachineStateActivityAuthorityStore StateActivityStore,
        IProductionContextActivityReader ActivityReader);
}
