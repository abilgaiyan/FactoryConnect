using System.Net;
using FactoryConnect.Api;
using FactoryConnect.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactoryConnect.Integration.Tests;

public sealed class ApiPersistenceStartupTests
{
    [Fact]
    public async Task SuccessfulGateRunsBeforeHostActivation()
    {
        var order = new List<string>();
        var gate = new DelegateStartupGate(cancellationToken =>
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            order.Add("gate");
            return ValueTask.CompletedTask;
        });
        await using var services = new ServiceCollection()
            .AddSingleton<IPersistenceStartupGate>(gate)
            .BuildServiceProvider();

        await ApiPersistenceStartup.RunAsync(
            services,
            () =>
            {
                order.Add("host");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["gate", "host"], order);
    }

    [Fact]
    public async Task GateFailurePreventsHostActivationAndPropagatesUnchanged()
    {
        var failure = new InvalidOperationException("startup gate failed");
        var hostActivated = false;
        var gate = new DelegateStartupGate(
            _ => ValueTask.FromException(failure));
        await using var services = new ServiceCollection()
            .AddSingleton<IPersistenceStartupGate>(gate)
            .BuildServiceProvider();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ApiPersistenceStartup.RunAsync(
                services,
                () =>
                {
                    hostActivated = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.False(hostActivated);
    }

    [Fact]
    public async Task GateCancellationPreventsHostActivationAndPropagatesUnchanged()
    {
        var cancellation = new OperationCanceledException("startup gate cancelled");
        var hostActivated = false;
        var gate = new DelegateStartupGate(
            _ => ValueTask.FromException(cancellation));
        await using var services = new ServiceCollection()
            .AddSingleton<IPersistenceStartupGate>(gate)
            .BuildServiceProvider();

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => ApiPersistenceStartup.RunAsync(
                services,
                () =>
                {
                    hostActivated = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Same(cancellation, actual);
        Assert.False(hostActivated);
    }

    [Fact]
    public async Task DefaultInMemoryApiStartsAndExposesHealthAfterGateSuccess()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void FailingGatePreventsApiHostCreation()
    {
        var failure = new InvalidOperationException("startup gate failed before API activation");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IPersistenceStartupGate>();
                    services.AddSingleton<IPersistenceStartupGate>(
                        new DelegateStartupGate(_ => ValueTask.FromException(failure)));
                }));

        var actual = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(actual);
        Assert.True(
            ContainsException(actual, failure),
            $"Expected API startup failure chain to contain the injected startup-gate exception. Actual: {actual}");
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

    private sealed class DelegateStartupGate(
        Func<CancellationToken, ValueTask> ensureReady)
        : IPersistenceStartupGate
    {
        public ValueTask EnsureReadyAsync(CancellationToken cancellationToken) =>
            ensureReady(cancellationToken);
    }
}
