using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class FactoryConnectWorkerTests
{
    [Fact]
    public async Task WorkerCreatesAndRunsAcquisitionRuntime()
    {
        var runtime = new RecordingRuntime();
        var factory = new RecordingRuntimeFactory(runtime);
        var worker = new FactoryConnectWorker(
            factory,
            NullLogger<FactoryConnectWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(1));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, runtime.RunCount);
        Assert.True(runtime.ObservedCancellation);
    }

    private sealed class RecordingRuntimeFactory(
        IMtConnectAcquisitionRuntime runtime)
        : IMtConnectAcquisitionRuntimeFactory
    {
        public int CreateCount { get; private set; }

        public ValueTask<IMtConnectAcquisitionRuntime> CreateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;

            return ValueTask.FromResult(runtime);
        }
    }

    private sealed class RecordingRuntime :
        IMtConnectAcquisitionRuntime
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public int RunCount { get; private set; }

        public bool ObservedCancellation { get; private set; }

        public async Task RunAsync(
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            _started.TrySetResult();

            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
            }
        }
    }
}
