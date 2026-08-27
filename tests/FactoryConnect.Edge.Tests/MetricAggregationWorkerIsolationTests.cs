using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MetricAggregationWorkerIsolationTests
{
    [Fact]
    public async Task FailingMachineDoesNotStopAnotherMachineAndCanRecoverInPlace()
    {
        var machineOne = new MachineId(Guid.NewGuid());
        var machineTwo = new MachineId(Guid.NewGuid());
        var streamOne = MetricInputStreamId.ForMachine(machineOne);
        var streamTwo = MetricInputStreamId.ForMachine(machineTwo);
        var reader = new FaultingMetricInputReader(streamOne, failuresBeforeSuccess: 2);
        reader.Add(CreatePositionedFact(machineOne, "machine-one", 10m));
        reader.Add(CreatePositionedFact(machineTwo, "machine-two", 20m));
        var innerStore = new InMemoryMetricAggregationStore();
        var store = new SignalingAggregationStore(innerStore);
        var runtimes = new MetricAggregationProcessingRuntimeSet(
            [
                new MetricAggregationProcessingRuntime(
                    new MetricAggregationProcessorId("metric-aggregation:machine-one"),
                    reader,
                    store,
                    streamOne,
                    batchSize: 10),
                new MetricAggregationProcessingRuntime(
                    new MetricAggregationProcessorId("metric-aggregation:machine-two"),
                    reader,
                    store,
                    streamTwo,
                    batchSize: 10),
            ],
            TimeSpan.FromMilliseconds(5));
        var worker = new MetricAggregationProcessingWorker(
            runtimes,
            NullLogger<MetricAggregationProcessingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.WaitForCheckpointAsync(
                new MetricAggregationProcessorId("metric-aggregation:machine-two"),
                TimeSpan.FromSeconds(5));

            var machineOneBeforeRecovery = await innerStore.ReadCheckpointAsync(
                new MetricAggregationProcessorId("metric-aggregation:machine-one"),
                streamOne,
                CancellationToken.None);
            Assert.Null(machineOneBeforeRecovery);

            await store.WaitForCheckpointAsync(
                new MetricAggregationProcessorId("metric-aggregation:machine-one"),
                TimeSpan.FromSeconds(5));

            var machineOneCheckpoint = await innerStore.ReadCheckpointAsync(
                new MetricAggregationProcessorId("metric-aggregation:machine-one"),
                streamOne,
                CancellationToken.None);
            var machineTwoCheckpoint = await innerStore.ReadCheckpointAsync(
                new MetricAggregationProcessorId("metric-aggregation:machine-two"),
                streamTwo,
                CancellationToken.None);

            Assert.Equal(new MetricInputPosition(1), machineOneCheckpoint!.Position);
            Assert.Equal(new MetricInputPosition(1), machineTwoCheckpoint!.Position);
            Assert.Equal(1, store.SuccessfulCommitCount(
                new MetricAggregationProcessorId("metric-aggregation:machine-two")));
            Assert.True(reader.FailureCount >= 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static PositionedMetricInputFact CreatePositionedFact(
        MachineId machineId,
        string factId,
        decimal value)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart =
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = value,
            Unit = "seconds",
            StartsAtUtc = occurrenceStart,
            EndsAtUtc = occurrenceStart.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            ShiftId = shiftId,
            ShiftScheduleAssignmentId = scheduleId,
        };

        return new PositionedMetricInputFact(
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(1),
            fact,
            new ShiftOccurrenceId(
                siteId,
                scheduleId,
                shiftId,
                occurrenceStart,
                occurrenceStart.AddHours(8)),
            new ProductionDayId(siteId, new DateOnly(2026, 8, 27)));
    }

    private sealed class FaultingMetricInputReader(
        MetricInputStreamId faultingStream,
        int failuresBeforeSuccess) : IMetricInputReader
    {
        private readonly Dictionary<MetricInputStreamId, PositionedMetricInputFact> _facts = [];
        private int _remainingFailures = failuresBeforeSuccess;

        public int FailureCount { get; private set; }

        public void Add(PositionedMetricInputFact fact) => _facts.Add(fact.StreamId, fact);

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.StreamId == faultingStream && _remainingFailures > 0)
            {
                _remainingFailures--;
                FailureCount++;
                throw new InvalidOperationException("Injected machine reader failure.");
            }

            var fact = _facts[request.StreamId];
            if (request.AfterPosition is not null)
            {
                return ValueTask.FromResult(
                    new MetricInputReadBatch(
                        request.StreamId,
                        request.AfterPosition,
                        request.AfterPosition,
                        []));
            }

            return ValueTask.FromResult(
                new MetricInputReadBatch(
                    request.StreamId,
                    afterPosition: null,
                    fact.Position,
                    [fact]));
        }
    }

    private sealed class SignalingAggregationStore(
        InMemoryMetricAggregationStore inner) : IMetricAggregationStore
    {
        private readonly Dictionary<MetricAggregationProcessorId, int> _commitCounts = [];
        private readonly Dictionary<MetricAggregationProcessorId, TaskCompletionSource> _signals = [];
        private readonly object _sync = new();

        public ValueTask<MetricAggregationCheckpoint?> ReadCheckpointAsync(
            MetricAggregationProcessorId processorId,
            MetricInputStreamId streamId,
            CancellationToken cancellationToken) =>
            inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);

        public ValueTask<MetricAggregateValue?> ReadShiftAggregateAsync(
            MetricAggregationProcessorId processorId,
            ShiftMetricAggregateKey key,
            CancellationToken cancellationToken) =>
            inner.ReadShiftAggregateAsync(processorId, key, cancellationToken);

        public ValueTask<MetricAggregateValue?> ReadProductionDayAggregateAsync(
            MetricAggregationProcessorId processorId,
            ProductionDayMetricAggregateKey key,
            CancellationToken cancellationToken) =>
            inner.ReadProductionDayAggregateAsync(processorId, key, cancellationToken);

        public async ValueTask CommitAsync(
            MetricAggregationCommit commit,
            CancellationToken cancellationToken)
        {
            await inner.CommitAsync(commit, cancellationToken);
            lock (_sync)
            {
                _commitCounts.TryGetValue(commit.ProcessorId, out var count);
                _commitCounts[commit.ProcessorId] = count + 1;
                if (_signals.TryGetValue(commit.ProcessorId, out var signal))
                {
                    signal.TrySetResult();
                }
            }
        }

        public int SuccessfulCommitCount(MetricAggregationProcessorId processorId)
        {
            lock (_sync)
            {
                return _commitCounts.TryGetValue(processorId, out var count) ? count : 0;
            }
        }

        public async Task WaitForCheckpointAsync(
            MetricAggregationProcessorId processorId,
            TimeSpan timeout)
        {
            TaskCompletionSource signal;
            lock (_sync)
            {
                if (_commitCounts.ContainsKey(processorId))
                {
                    return;
                }

                if (!_signals.TryGetValue(processorId, out signal!))
                {
                    signal = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _signals.Add(processorId, signal);
                }
            }

            await signal.Task.WaitAsync(timeout);
        }
    }
}
