using FactoryConnect.Edge;
using FactoryConnect.EdgeQualification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class QualificationHarnessSafetyTests
{
    [Theory]
    [InlineData("FactoryConnect")]
    [InlineData("FactoryConnect_P0_20261105")]
    [InlineData("FactoryConnect_P0_Qualification_20261106_other")]
    public void RejectsEveryNonQualificationDatabase(string database) =>
        Assert.Throws<InvalidOperationException>(() => QualificationHarness.ValidateTarget(
            $"Server={QualificationHarness.Server};Database={database};Integrated Security=True"));

    [Theory]
    [InlineData("Server=factory-server;Database=FactoryConnect_P0_Qualification_20261106;Integrated Security=True")]
    [InlineData("Server=DESKTOP-1PJK2OB\\FCINVENTORY;Database=FactoryConnect_P0_Qualification_20261106;User ID=test;Password=test")]
    [InlineData("Server=DESKTOP-1PJK2OB\\FCINVENTORY;Database=FactoryConnect_P0_Qualification_20261106;Integrated Security=True;Failover Partner=factory-server")]
    public void RejectsOtherServerAuthenticationAndFailover(string settings) =>
        Assert.Throws<InvalidOperationException>(() => QualificationHarness.ValidateTarget(settings));

    [Fact]
    public void AllowsOnlyNamedLocalTarget() => QualificationHarness.ValidateTarget(
        $"Server={QualificationHarness.Server};Database={QualificationHarness.Database};Integrated Security=True");

    [Fact]
    public void RemovesOnlyAcquisitionAndKeepsProcessingRegistrations()
    {
        var services = Registrations();
        var original = services.Where(item => item.ImplementationType != typeof(FactoryConnectWorker)).ToArray();
        QualificationHarness.DisableAcquisition(services);
        Assert.Equal(original, services.ToArray());
        Assert.DoesNotContain(services, item => item.ImplementationType == typeof(FactoryConnectWorker));
    }

    [Fact]
    public void RefusesMissingAcquisitionOrMissingProcessingWorker()
    {
        var services = Registrations();
        services.Remove(Assert.Single(services, item => item.ImplementationType == typeof(FactoryConnectWorker)));
        Assert.Throws<InvalidOperationException>(() => QualificationHarness.DisableAcquisition(services));
        services = Registrations();
        services.Remove(Assert.Single(services, item => item.ImplementationType == typeof(DurableObservationProcessingWorker)));
        Assert.Throws<InvalidOperationException>(() => QualificationHarness.DisableAcquisition(services));
    }

    [Fact]
    public void ExplicitJsonConfigurationPreservesInventoryAndOverridesOnlyPersistenceTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fc-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{\"ObservationProcessing\":{\"BatchSize\":100}}");
            var path = Path.Combine(directory, "commissioned.json");
            var json = """
                {"PersistenceProviders":{"SqlServer":{"ConnectionString":"Server=production;Database=FactoryConnect"}},
                 "MTConnect":{"Machines":[
                   {"MachineId":"677b030b-1ede-4a67-82ae-6a494791357f","BaseUri":"http://127.0.0.1:5001"},
                   {"MachineId":"bfa0dc97-9a68-45ed-b189-1dacb7d863f0","BaseUri":"http://127.0.0.1:5002"}]}}
                """;
            File.WriteAllText(path, json);
            var target = $"Server={QualificationHarness.Server};Database={QualificationHarness.Database};Integrated Security=True";
            using var config = (Microsoft.Extensions.Configuration.ConfigurationRoot)QualificationHarness.LoadConfiguration(path, directory, target);
            Assert.Equal(target, config["PersistenceProviders:SqlServer:ConnectionString"]);
            Assert.Equal("http://127.0.0.1:5001", config["MTConnect:Machines:0:BaseUri"]);
            Assert.Equal("100", config["ObservationProcessing:BatchSize"]);
            Assert.Equal(json, File.ReadAllText(path));
            Assert.DoesNotContain(config.Providers, provider => provider.GetType().Name.Contains("EnvironmentVariables", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceCollection Registrations()
    {
        var services = new ServiceCollection();
        // Registration only: tests never construct or start a host, worker or SQL connection.
        services.AddHostedService<FactoryConnectWorker>();
        services.AddHostedService<DurableObservationProcessingWorker>();
        services.AddHostedService<ProductionMetricInputProcessingWorker>();
        services.AddHostedService<MetricAggregationProcessingWorker>();
        services.AddHostedService<OperationalMetricProjectionProcessingWorker>();
        return services;
    }
}
