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
        var startupCancellation = new TestStartupCancellationRegistration();
        await using var services = CreateServices(gate, startupCancellation);

        await ApiPersistenceStartup.RunAsync(
            services,
            () =>
            {
                Assert.True(startupCancellation.IsDisposed);
                order.Add("host");
                return Task.CompletedTask;
            });

        Assert.Equal(["gate", "host"], order);
        Assert.True(startupCancellation.IsDisposed);
    }

    [Fact]
    public async Task GateFailurePreventsHostActivationAndPropagatesUnchanged()
    {
        var failure = new InvalidOperationException("startup gate failed");
        var hostActivated = false;
        var gate = new DelegateStartupGate(
            _ => ValueTask.FromException(failure));
        var startupCancellation = new TestStartupCancellationRegistration();
        await using var services = CreateServices(gate, startupCancellation);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ApiPersistenceStartup.RunAsync(
                services,
                () =>
                {
                    hostActivated = true;
                    return Task.CompletedTask;
                }));

        Assert.Same(failure, actual);
        Assert.False(hostActivated);
        Assert.True(startupCancellation.IsDisposed);
    }

    [Fact]
    public async Task GateCancellationPreventsHostActivationAndPropagatesUnchanged()
    {
        var cancellation = new OperationCanceledException("startup gate cancelled");
        var hostActivated = false;
        var gate = new DelegateStartupGate(
            _ => ValueTask.FromException(cancellation));
        var startupCancellation = new TestStartupCancellationRegistration();
        await using var services = CreateServices(gate, startupCancellation);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => ApiPersistenceStartup.RunAsync(
                services,
                () =>
                {
                    hostActivated = true;
                    return Task.CompletedTask;
                }));

        Assert.Same(cancellation, actual);
        Assert.False(hostActivated);
        Assert.True(startupCancellation.IsDisposed);
    }

    [Fact]
    public async Task StartupCancellationTokenReachesGateAndPreventsHostActivation()
    {
        var hostActivated = false;
        var tokenObserved = false;
        var gate = new DelegateStartupGate(cancellationToken =>
        {
            tokenObserved = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });
        var startupCancellation = new TestStartupCancellationRegistration();
        startupCancellation.Cancel();
        await using var services = CreateServices(gate, startupCancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ApiPersistenceStartup.RunAsync(
                services,
                () =>
                {
                    hostActivated = true;
                    return Task.CompletedTask;
                }));

        Assert.True(tokenObserved);
        Assert.False(hostActivated);
        Assert.True(startupCancellation.IsDisposed);
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

    [Fact]
    public void CancelledEntryPointStartupPreventsApiHostCreation()
    {
        var gate = new ObservingCancellationStartupGate();
        var startupCancellation = new TestStartupCancellationRegistration();
        startupCancellation.Cancel();
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IPersistenceStartupGate>();
                    services.RemoveAll<IApiStartupCancellationRegistrationFactory>();
                    services.AddSingleton<IPersistenceStartupGate>(gate);
                    services.AddSingleton<IApiStartupCancellationRegistrationFactory>(
                        new TestStartupCancellationRegistrationFactory(startupCancellation));
                }));

        var actual = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(actual);
        Assert.True(gate.ObservedCancelledToken);
        Assert.True(startupCancellation.IsDisposed);
        Assert.True(
            ContainsException<OperationCanceledException>(actual),
            $"Expected API startup cancellation to propagate through host creation. Actual: {actual}");
    }

    private static ServiceProvider CreateServices(
        IPersistenceStartupGate gate,
        TestStartupCancellationRegistration startupCancellation) =>
        new ServiceCollection()
            .AddSingleton<IPersistenceStartupGate>(gate)
            .AddSingleton<IApiStartupCancellationRegistrationFactory>(
                new TestStartupCancellationRegistrationFactory(startupCancellation))
            .BuildServiceProvider();

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

    private static bool ContainsException<TException>(Exception actual)
        where TException : Exception
    {
        if (actual is TException)
        {
            return true;
        }

        if (actual is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(ContainsException<TException>))
        {
            return true;
        }

        return actual.InnerException is not null &&
            ContainsException<TException>(actual.InnerException);
    }

    private sealed class DelegateStartupGate(
        Func<CancellationToken, ValueTask> ensureReady)
        : IPersistenceStartupGate
    {
        public ValueTask EnsureReadyAsync(CancellationToken cancellationToken) =>
            ensureReady(cancellationToken);
    }

    private sealed class ObservingCancellationStartupGate : IPersistenceStartupGate
    {
        public bool ObservedCancelledToken { get; private set; }

        public ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
        {
            ObservedCancelledToken = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestStartupCancellationRegistrationFactory(
        TestStartupCancellationRegistration registration)
        : IApiStartupCancellationRegistrationFactory
    {
        public IApiStartupCancellationRegistration Create() => registration;
    }

    private sealed class TestStartupCancellationRegistration :
        IApiStartupCancellationRegistration
    {
        private readonly CancellationTokenSource _source = new();

        public CancellationToken Token => _source.Token;

        public bool IsDisposed { get; private set; }

        public void Cancel() => _source.Cancel();

        public void Dispose()
        {
            IsDisposed = true;
            _source.Dispose();
        }
    }
}
