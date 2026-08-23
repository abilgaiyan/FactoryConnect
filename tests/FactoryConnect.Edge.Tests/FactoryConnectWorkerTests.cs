using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class FactoryConnectWorkerTests
{
    [Fact]
    public async Task WorkerDelegatesExecutionToAcquisitionRuntime()
    {
        var runtime = new RecordingRuntime();
        var worker = new FactoryConnectWorker(
            runtime,
            NullLogger<FactoryConnectWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await runtime.Started.WaitAsync(TimeSpan.FromSeconds(1));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, runtime.RunCount);
        Assert.True(runtime.ObservedCancellation);
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
