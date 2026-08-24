using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class PersistenceProviderSelectionTests
{
    [Fact]
    public void ProviderKey_NormalizesWhitespaceAndCase()
    {
        var key = PersistenceProviderKey.Normalize("  inMemory  ");

        Assert.Equal("INMEMORY", key);
    }

    [Fact]
    public void AddFactoryConnectPersistence_RequiresProviderConfiguration()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal("Persistence:Provider is required.", exception.Message);
    }

    [Fact]
    public void ResolvingStore_ActivatesOnlySelectedProvider()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("primary");
        var primaryActivations = 0;
        var secondaryActivations = 0;

        services.AddFactoryConnectPersistence(configuration);
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Primary",
                _ =>
                {
                    primaryActivations++;
                    return new InMemoryObservationIngestionStore();
                }));
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Secondary",
                _ =>
                {
                    secondaryActivations++;
                    return new InMemoryObservationIngestionStore();
                }));

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IObservationIngestionStore>();
        var second = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.Same(first, second);
        Assert.Equal(1, primaryActivations);
        Assert.Equal(0, secondaryActivations);
    }

    [Fact]
    public void RegisteredProvider_IsNotActivatedUntilStoreIsResolved()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("primary");
        var activations = 0;

        services.AddFactoryConnectPersistence(configuration);
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Primary",
                _ =>
                {
                    activations++;
                    return new InMemoryObservationIngestionStore();
                }));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(0, activations);
    }

    [Fact]
    public void ResolvingStore_RejectsUnknownProvider()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("missing");

        services.AddFactoryConnectPersistence(configuration);
        services.AddInMemoryPersistenceProvider();

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IObservationIngestionStore>());

        Assert.Equal(
            "Persistence provider 'MISSING' is not registered.",
            exception.Message);
    }

    [Fact]
    public void ResolvingStore_RejectsDuplicateNormalizedProviderKeys()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("primary");

        services.AddFactoryConnectPersistence(configuration);
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                "Primary",
                static _ => new InMemoryObservationIngestionStore()));
        services.AddPersistenceProvider(
            new PersistenceProviderRegistration(
                " primary ",
                static _ => new InMemoryObservationIngestionStore()));

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IObservationIngestionStore>());

        Assert.Equal(
            "Persistence provider key 'PRIMARY' is registered more than once.",
            exception.Message);
    }

    [Fact]
    public void AddFactoryConnectPersistence_RejectsPreRegisteredStore()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("inmemory");

        services.AddSingleton<IObservationIngestionStore>(
            new InMemoryObservationIngestionStore());

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectPersistence(configuration));

        Assert.Equal(
            "IObservationIngestionStore is already registered. " +
            "Persistence activation must own the single store registration.",
            exception.Message);
    }

    [Fact]
    public void InMemoryProvider_IsActivatedThroughProviderRegistration()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration(" InMemory ");

        services.AddFactoryConnectPersistence(configuration);
        services.AddInMemoryPersistenceProvider();

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.IsType<InMemoryObservationIngestionStore>(store);
    }

    private static IConfiguration BuildConfiguration(string? provider = null)
    {
        Dictionary<string, string?> values = [];

        if (provider is not null)
        {
            values["Persistence:Provider"] = provider;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
