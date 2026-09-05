namespace FactoryConnect.Persistence;

public sealed class PersistenceProviderRegistration :
    IPersistenceProviderRegistration
{
    private readonly Func<
        IServiceProvider,
        PersistenceProviderServices> _factory;
    private readonly Func<
        IServiceProvider,
        IPersistenceStartupGate> _startupGateFactory;

    public PersistenceProviderRegistration(
        string providerKey,
        Func<IServiceProvider, PersistenceProviderServices> factory)
        : this(
            providerKey,
            PersistenceProviderCapabilities.Core,
            factory,
            static _ => NoOpPersistenceStartupGate.Instance)
    {
    }

    public PersistenceProviderRegistration(
        string providerKey,
        PersistenceProviderCapabilities capabilities,
        Func<IServiceProvider, PersistenceProviderServices> factory)
        : this(
            providerKey,
            capabilities,
            factory,
            static _ => NoOpPersistenceStartupGate.Instance)
    {
    }

    public PersistenceProviderRegistration(
        string providerKey,
        PersistenceProviderCapabilities capabilities,
        Func<IServiceProvider, PersistenceProviderServices> factory,
        Func<IServiceProvider, IPersistenceStartupGate> startupGateFactory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(startupGateFactory);

        ProviderKey = PersistenceProviderKey.Normalize(providerKey);
        Capabilities = capabilities;
        _factory = factory;
        _startupGateFactory = startupGateFactory;
    }

    public string ProviderKey { get; }

    public PersistenceProviderCapabilities Capabilities { get; }

    public PersistenceProviderServices Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return _factory(services)
            ?? throw new InvalidOperationException(
                $"Persistence provider '{ProviderKey}' returned no services.");
    }

    public IPersistenceStartupGate CreateStartupGate(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return _startupGateFactory(services)
            ?? throw new InvalidOperationException(
                $"Persistence provider '{ProviderKey}' returned no startup gate.");
    }
}

internal sealed class NoOpPersistenceStartupGate : IPersistenceStartupGate
{
    public static NoOpPersistenceStartupGate Instance { get; } = new();

    private NoOpPersistenceStartupGate()
    {
    }

    public ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
