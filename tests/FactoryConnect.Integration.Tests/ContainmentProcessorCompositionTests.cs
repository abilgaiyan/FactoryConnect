using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class ContainmentProcessorCompositionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SqlCompositionUsesOnlyTheExplicitlySelectedIdentity(bool containment)
    {
        var machine = new MachineId(Guid.NewGuid());
        using var provider = Build("Server=localhost;Database=not-opened;Integrated Security=True;TrustServerCertificate=True", machine, containment);
        var runtime = Assert.Single(provider.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>().Runtimes);
        Assert.Equal(containment ? AvailabilityContainmentV1.ProjectionProcessorId(machine) :
            new OperationalMetricProjectionProcessorId($"operational-metrics:{machine.Value}:builtins-v1"), runtime.ProcessorId);
        Assert.IsType<SqlServerOperationalMetricContainmentPublisher>(provider.GetRequiredService<PersistenceProviderServices>().OperationalMetricContainmentPublisher);
    }

    [Fact]
    public void UnsupportedPolicyFailsBeforeRuntimeCreation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["OperationalMetrics:PollingInterval"] = "00:00:01", ["OperationalMetrics:EvaluationPolicyId"] = "unsupported" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddFactoryConnectOperationalMetrics(configuration, [new MachineId(Guid.NewGuid())]));
    }

    [Fact]
    public void ProviderWithoutContainmentSupportCannotSilentlyRunLegacy()
    {
        var machine = new MachineId(Guid.NewGuid());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Persistence:Provider"] = "InMemory", ["OperationalMetrics:PollingInterval"] = "00:00:01",
          ["OperationalMetrics:EvaluationPolicyId"] = AvailabilityContainmentV1.PolicyId }).Build();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration, PersistenceProviderCapabilities.OperationalMetrics);
        services.AddFactoryConnectOperationalMetrics(configuration, [machine]);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>());
    }

    internal static ServiceProvider Build(string connection, MachineId machine, bool containment = true)
    {
        var values = new Dictionary<string,string?> { ["Persistence:Provider"] = "SqlServer",
            [SqlServerPersistenceOptions.SectionName + ":ConnectionString"] = connection,
            ["OperationalMetrics:PollingInterval"] = "00:00:01" };
        if (containment) values["OperationalMetrics:EvaluationPolicyId"] = AvailabilityContainmentV1.PolicyId;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration, PersistenceProviderCapabilities.OperationalMetrics);
        services.AddFactoryConnectOperationalMetrics(configuration, [machine]);
        return services.BuildServiceProvider();
    }
}
