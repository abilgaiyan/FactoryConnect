using System.Net;
using FactoryConnect.Api;
using FactoryConnect.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    private sealed class DelegateStartupGate(
        Func<CancellationToken, ValueTask> ensureReady)
        : IPersistenceStartupGate
    {
        public ValueTask EnsureReadyAsync(CancellationToken cancellationToken) =>
            ensureReady(cancellationToken);
    }
}
