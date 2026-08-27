using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionMetricInputWorkerIsolationTests
{
    [Fact]
    public async Task FailingMachineProducerDoesNotBlockAnotherMachineAndRecoversInPlace()
    {
        var machineA = new MachineId(Guid.NewGuid());
        var machineB = new MachineId(Guid.NewGuid());
        var streamA = new ObservationStreamId(machineA, "quantity-a");
        var streamB = new ObservationStreamId(machineB, "quantity-b");
        var processorA = new ObservationProcessorId("quantity-a");
        var processorB = new ObservationProcessorId("quantity-b");
        var reader = new InMemoryProductionQuantityEvidenceReader();
        reader.Add(CreateEvidence(machineA, streamA, "QA"));
        reader.Add(CreateEvidence(machineB, streamB, "QB"));

        var shiftResolver = new ShiftOccurrenceResolver(
            new InMemoryShiftScheduleReader(
            [
                new ShiftScheduleAssignment
                {
                    Id = new ShiftScheduleAssignmentId("SHIFT-SCHEDULE"),
                    CompanyId = new CompanyId("COMP-1"),
                    SiteId = new SiteId("SITE-1"),
                    TimeZoneId = new FactoryTimeZoneId("UTC"),
                    ShiftId = new ShiftId("SHIFT-1"),
                    Name = "Shift 1",
                    StartsAtLocal = new TimeOnly(6, 0),
                    EndsAtLocal = new TimeOnly(14, 0),
                    EffectiveFrom = new DateOnly(2026, 1, 1),
                },
            ]));
        var durableStore = new InMemoryProductionContextProcessingStore();
        var controlledStore = new ControllableProductionStore(
            durableStore,
            processorA);
        var runtimeA = new ProductionQuantityFactProcessingRuntime(
            processorA,
            reader,
            shiftResolver,
            controlledStore,
            streamA,
            batchSize: 10);
        var runtimeB = new ProductionQuantityFactProcessingRuntime(
            processorB,
            reader,
            shiftResolver,
            controlledStore,
            streamB,
            batchSize: 10);
        var runtimeSet = new ProductionMetricInputRuntimeSet(
            [],
            [runtimeA, runtimeB],
            TimeSpan.FromMilliseconds(10));
        var worker = new ProductionMetricInputProcessingWorker(
            runtimeSet,
            NullLogger<ProductionMetricInputProcessingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await controlledStore.MachineBCommitted.WaitAsync(
                TimeSpan.FromSeconds(5));

            var checkpointA = await durableStore.ReadCheckpointAsync(
                processorA,
                streamA,
                CancellationToken.None);
            var checkpointB = await durableStore.ReadCheckpointAsync(
                processorB,
                streamB,
                CancellationToken.None);
            Assert.Null(checkpointA);
            Assert.NotNull(checkpointB);

            var aggregationStore = new InMemoryMetricAggregationStore();
            var aggregationB = new MetricAggregationProcessingRuntime(
                new MetricAggregationProcessorId("aggregation-b"),
                durableStore,
                aggregationStore,
                MetricInputStreamId.ForMachine(machineB),
                batchSize: 10);
            Assert.Equal(
                1,
                await aggregationB.RunCycleAsync(CancellationToken.None));

            controlledStore.AllowMachineA();
            await controlledStore.MachineACommitted.WaitAsync(
                TimeSpan.FromSeconds(5));

            checkpointA = await durableStore.ReadCheckpointAsync(
                processorA,
                streamA,
                CancellationToken.None);
            Assert.NotNull(checkpointA);

            var aggregationA = new MetricAggregationProcessingRuntime(
                new MetricAggregationProcessorId("aggregation-a"),
                durableStore,
                aggregationStore,
                MetricInputStreamId.ForMachine(machineA),
                batchSize: 10);
            Assert.Equal(
                1,
                await aggregationA.RunCycleAsync(CancellationToken.None));
            Assert.Equal(
                0,
                await aggregationB.RunCycleAsync(CancellationToken.None));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static DurableProductionQuantityEvidence CreateEvidence(
        MachineId machineId,
        ObservationStreamId streamId,
        string id) =>
        new(
            new ObservationPosition(1),
            streamId,
            new ProductionQuantityEvidence
            {
                Id = new ProductionQuantityEvidenceId(id),
                CompanyId = new CompanyId("COMP-1"),
                SiteId = new SiteId("SITE-1"),
                MachineId = machineId,
                ShiftId = new ShiftId("SHIFT-1"),
                OccurredAtUtc = new DateTimeOffset(
                    2026,
                    8,
                    27,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                GoodQuantity = 1,
            });

    private sealed class ControllableProductionStore :
        IProductionContextProcessingStore
    {
        private readonly InMemoryProductionContextProcessingStore _inner;
        private readonly ObservationProcessorId _failingProcessor;
        private readonly TaskCompletionSource<bool> _machineACommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _machineBCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _allowMachineA;

        public ControllableProductionStore(
            InMemoryProductionContextProcessingStore inner,
            ObservationProcessorId failingProcessor)
        {
            _inner = inner;
            _failingProcessor = failingProcessor;
        }

        public Task MachineACommitted => _machineACommitted.Task;

        public Task MachineBCommitted => _machineBCommitted.Task;

        public void AllowMachineA() => _allowMachineA = true;

        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken) =>
            _inner.ReadCheckpointAsync(
                processorId,
                streamId,
                cancellationToken);

        public async Task CommitAsync(
            ProductionContextProcessingCommit commit,
            CancellationToken cancellationToken)
        {
            if (commit.NextCheckpoint.ProcessorId == _failingProcessor &&
                !_allowMachineA)
            {
                throw new InvalidOperationException(
                    "Injected machine A producer failure.");
            }

            await _inner.CommitAsync(commit, cancellationToken);

            if (commit.NextCheckpoint.ProcessorId == _failingProcessor)
            {
                _machineACommitted.TrySetResult(true);
            }
            else
            {
                _machineBCommitted.TrySetResult(true);
            }
        }
    }
}
