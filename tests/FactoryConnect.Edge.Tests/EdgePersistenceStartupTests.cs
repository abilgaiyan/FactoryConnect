using FactoryConnect.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgePersistenceStartupTests
{
    [Fact]
    public async Task SuccessfulGateRunsBeforeHostedServiceActivation()
    {
        var order = new List<string>();
        var gate = new DelegateStartupGate(cancellationToken =>
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            order.Add("gate");
            return ValueTask.CompletedTask;
        });
        var worker = new ProbeHostedService(() => order.Add("worker"));
        var startupCancellation = new TestStartupCancellationRegistration();
        await using var host = CreateHost(gate, worker, startupCancellation);

        await EdgePersistenceStartup.RunAsync(
            host.Services,
            () => host.StartAsync());

        try
        {
            Assert.Equal(["gate", "worker"], order);
            Assert.Equal(1, worker.StartCount);
            Assert.True(startupCancellation.IsDisposed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task GateFailurePreventsHostedServiceActivationAndPropagatesUnchanged()
    {
        var failure = new InvalidOperationException("startup gate failed");
        var gate = new DelegateStartupGate(_ => ValueTask.FromException(failure));
        var worker = new ProbeHostedService();
        var startupCancellation = new TestStartupCancellationRegistration();
        await using var host = CreateHost(gate, worker, startupCancellation);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EdgePersistenceStartup.RunAsync(
                host.Services,
                () => host.StartAsync()));

        Assert.Same(failure, actual);
        Assert.Equal(0, worker.StartCount);
        Assert.True(startupCancellation.IsDisposed);
    }

    [Fact]
    public async Task GateCancellationPreventsHostedServiceActivationAndPropagatesUnchanged()
    {
        var cancellation = new OperationCanceledException("startup gate cancelled");
        var gate = new DelegateStartupGate(_ => ValueTask.FromException(cancellation));
        var worker = new ProbeHostedService();
        var startupCancellation = new TestStartupCancellationRegistration();
        await using var host = CreateHost(gate, worker, startupCancellation);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => EdgePersistenceStartup.RunAsync(
                host.Services,
                () => host.StartAsync()));

        Assert.Same(cancellation, actual);
        Assert.Equal(0, worker.StartCount);
        Assert.True(startupCancellation.IsDisposed);
    }

    [Fact]
    public async Task StartupCancellationTokenReachesGateAndPreventsHostedServiceActivation()
    {
        var gate = new ObservingCancellationStartupGate();
        var worker = new ProbeHostedService();
        var startupCancellation = new TestStartupCancellationRegistration();
        startupCancellation.Cancel();
        await using var host = CreateHost(gate, worker, startupCancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => EdgePersistenceStartup.RunAsync(
                host.Services,
                () => host.StartAsync()));

        Assert.True(gate.ObservedCancelledToken);
        Assert.Equal(0, worker.StartCount);
        Assert.True(startupCancellation.IsDisposed);
    }

    [Fact]
    public async Task PreActivationCancellationRegistrationIsDisposedBeforeWorkerStarts()
    {
        var startupCancellation = new TestStartupCancellationRegistration();
        var gate = new DelegateStartupGate(_ => ValueTask.CompletedTask);
        var worker = new ProbeHostedService(() =>
            Assert.True(startupCancellation.IsDisposed));
        await using var host = CreateHost(gate, worker, startupCancellation);

        await EdgePersistenceStartup.RunAsync(
            host.Services,
            () => host.StartAsync());

        try
        {
            Assert.Equal(1, worker.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost CreateHost(
        IPersistenceStartupGate gate,
        ProbeHostedService worker,
        TestStartupCancellationRegistration startupCancellation)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IPersistenceStartupGate>(gate);
        builder.Services.AddSingleton<IHostedService>(worker);
        builder.Services.AddSingleton<IEdgeStartupCancellationRegistrationFactory>(
            new TestStartupCancellationRegistrationFactory(startupCancellation));
        return builder.Build();
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

    private sealed class ProbeHostedService(Action? onStart = null) : IHostedService
    {
        public int StartCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            onStart?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestStartupCancellationRegistrationFactory(
        TestStartupCancellationRegistration registration)
        : IEdgeStartupCancellationRegistrationFactory
    {
        public IEdgeStartupCancellationRegistration Create() => registration;
    }

    private sealed class TestStartupCancellationRegistration :
        IEdgeStartupCancellationRegistration
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
