using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.Integration.Tests;

public sealed class ApiCurrentStateCompositionTests
{
    [Fact]
    public void CompletedApiServiceGraphResolvesCurrentStateReaderWithoutAcquisitionOrProcessingRuntime()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<ICurrentMachineStateReader>());
        Assert.NotNull(services.GetRequiredService<ICurrentStateOwnerResolver>());
        Assert.NotNull(services.GetRequiredService<ICurrentStateFreshnessPolicy>());

        Assert.Null(services.GetService<IMtConnectAcquisitionRuntimeFactory>());
        Assert.Null(services.GetService<IMtConnectAcquisitionSessionFactory>());
        Assert.Null(services.GetService<DurableObservationProcessingPipeline>());
        Assert.Null(services.GetService<DurableObservationProcessingPipelineSet>());

        var hostedServices = services.GetServices<IHostedService>().ToArray();

        Assert.DoesNotContain(
            hostedServices,
            static service => service is FactoryConnectWorker);
        Assert.DoesNotContain(
            hostedServices,
            static service => service is DurableObservationProcessingWorker);
    }

    [Fact]
    public void MtConnectMachineConfigurationDoesNotActivateAcquisitionServices()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<ICurrentStateOwnerResolver>());
        Assert.Null(services.GetService<IMtConnectAcquisitionRuntimeFactory>());
        Assert.Null(services.GetService<IMtConnectAcquisitionSessionFactory>());

        var hostedServices = services.GetServices<IHostedService>().ToArray();
        Assert.DoesNotContain(
            hostedServices,
            static service => service is FactoryConnectWorker);
    }

    [Fact]
    public void StartupGateFailurePreventsCurrentStateApiActivation()
    {
        var failure = new InvalidOperationException(
            "current-state composition startup gate failed");

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IPersistenceStartupGate>();
                    services.AddSingleton<IPersistenceStartupGate>(
                        new FailingStartupGate(failure));
                }));

        var actual = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(actual);
        Assert.True(
            ContainsException(actual, failure),
            $"Expected API activation failure chain to contain the injected startup-gate exception. Actual: {actual}");
    }

    private static bool ContainsException(Exception actual, Exception expected)
    {
        if (ReferenceEquals(actual, expected))
        {
            return true;
        }

        if (actual is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(inner => ContainsException(inner, expected)))
        {
            return true;
        }

        return actual.InnerException is not null &&
            ContainsException(actual.InnerException, expected);
    }

    private sealed class FailingStartupGate(Exception failure)
        : IPersistenceStartupGate
    {
        public ValueTask EnsureReadyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException(failure);
    }
}
