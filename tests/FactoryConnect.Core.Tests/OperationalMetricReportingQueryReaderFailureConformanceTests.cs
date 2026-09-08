using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricReportingQueryReaderFailureConformanceTests
{
    [Fact]
    public async Task PublicReaderDoesNotRetryProviderFailure()
    {
        var expected = new InvalidOperationException("provider failure");
        var provider = new ThrowingProvider(expected);
        var reader = new OperationalMetricReportingQueryReader(provider);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadAsync(CreateQuery(), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(1, provider.ReadCount);
    }

    [Fact]
    public async Task PublicReaderPropagatesInFlightCancellationWithoutRetry()
    {
        var provider = new BlockingProvider();
        var reader = new OperationalMetricReportingQueryReader(provider);
        using var cancellation = new CancellationTokenSource();

        var read = reader.ReadAsync(CreateQuery(), cancellation.Token).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        Assert.Equal(1, provider.ReadCount);
    }

    private static ProductionDayOperationalMetricReportQuery CreateQuery()
    {
        var machineId = new MachineId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var processorId = new OperationalMetricProjectionProcessorId("reader-failure-conformance");
        return new ProductionDayOperationalMetricReportQuery(
            new OperationalMetricReportingSourceSelection([
                new OperationalMetricReportingSource(machineId, processorId),
            ]),
            new DateOnly(2026, 9, 7),
            new DateOnly(2026, 9, 8),
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(20));
    }

    private sealed class ThrowingProvider(Exception exception) :
        IOperationalMetricReportingQueryProvider
    {
        public int ReadCount { get; private set; }

        public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadWindowAsync(
            OperationalMetricReportQuery query,
            OperationalMetricEvaluationKey? startAfter,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromException<IReadOnlyList<OperationalMetricProjectionSummary>>(exception);
        }
    }

    private sealed class BlockingProvider : IOperationalMetricReportingQueryProvider
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount { get; private set; }

        public async ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadWindowAsync(
            OperationalMetricReportQuery query,
            OperationalMetricEvaluationKey? startAfter,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Array.Empty<OperationalMetricProjectionSummary>();
        }
    }
}
