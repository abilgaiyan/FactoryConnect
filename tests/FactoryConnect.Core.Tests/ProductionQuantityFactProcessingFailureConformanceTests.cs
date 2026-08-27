using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionQuantityFactProcessingFailureConformanceTests
{
    public static TheoryData<FailureBoundary> FailureBoundaries =>
        new()
        {
            FailureBoundary.CheckpointRestore,
            FailureBoundary.EvidenceRead,
            FailureBoundary.Commit,
        };

    [Theory]
    [MemberData(nameof(FailureBoundaries))]
    public async Task QuantityBoundaryFailurePropagatesWithoutProgressAndRetryProcessesExactlyOnce(
        FailureBoundary boundary)
    {
        var machineId = new MachineId(new Guid("77777777-7777-7777-7777-777777777777"));
        var streamId = new ObservationStreamId(machineId, "quantity");
        var processorId = new ObservationProcessorId("quantity-failure-conformance");
        var innerReader = new InMemoryProductionQuantityEvidenceReader();
        innerReader.Add(CreateDurableEvidence(machineId, streamId));
        var reader = new FailOnceQuantityReader(
            innerReader,
            boundary == FailureBoundary.EvidenceRead);
        var innerStore = new InMemoryProductionContextProcessingStore();
        var store = new FailOnceQuantityStore(
            innerStore,
            boundary == FailureBoundary.CheckpointRestore,
            boundary == FailureBoundary.Commit);
        var shiftResolver = CreateShiftResolver();
        var runtime = new ProductionQuantityFactProcessingRuntime(
            processorId,
            reader,
            shiftResolver,
            store,
            streamId,
            10);

        var error = await Assert.ThrowsAsync<InjectedQuantityBoundaryException>(() =>
            runtime.RunCycleAsync());
        Assert.Equal(boundary, error.Boundary);
        Assert.Empty(innerStore.MetricFacts);
        Assert.Empty(innerStore.PositionedMetricInputs);
        Assert.Null(await innerStore.ReadCheckpointAsync(
            processorId,
            streamId,
            CancellationToken.None));

        Assert.Equal(1, await runtime.RunCycleAsync());
        Assert.Equal(3, innerStore.MetricFacts.Count);
        Assert.Equal(3, innerStore.PositionedMetricInputs.Count);
        var checkpoint = await innerStore.ReadCheckpointAsync(
            processorId,
            streamId,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(new ObservationPosition(1), checkpoint.Position);

        var restarted = new ProductionQuantityFactProcessingRuntime(
            processorId,
            innerReader,
            shiftResolver,
            innerStore,
            streamId,
            10);
        Assert.Equal(0, await restarted.RunCycleAsync());
        Assert.Equal(3, innerStore.MetricFacts.Count);
        Assert.Equal(3, innerStore.PositionedMetricInputs.Count);
    }

    public enum FailureBoundary
    {
        CheckpointRestore,
        EvidenceRead,
        Commit,
    }

    private static ShiftOccurrenceResolver CreateShiftResolver()
    {
        var assignment = new ShiftScheduleAssignment
        {
            Id = new ShiftScheduleAssignmentId("SCHEDULE-1"),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            TimeZoneId = new FactoryTimeZoneId("Asia/Kolkata"),
            ShiftId = new ShiftId("SHIFT-1"),
            Name = "SHIFT-1",
            StartsAtLocal = new TimeOnly(6, 0),
            EndsAtLocal = new TimeOnly(15, 0),
            EffectiveFrom = new DateOnly(2026, 8, 26),
        };

        return new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader([assignment]));
    }

    private static DurableProductionQuantityEvidence CreateDurableEvidence(
        MachineId machineId,
        ObservationStreamId streamId) =>
        new(
            new ObservationPosition(1),
            streamId,
            new ProductionQuantityEvidence
            {
                Id = new ProductionQuantityEvidenceId("Q-FAILURE-1"),
                CompanyId = new CompanyId("COMP-1"),
                SiteId = new SiteId("SITE-1"),
                ProductionLineId = new ProductionLineId("LINE-1"),
                MachineId = machineId,
                ShiftId = new ShiftId("SHIFT-1"),
                OccurredAtUtc = new DateTimeOffset(2026, 8, 26, 8, 30, 0, TimeSpan.Zero),
                PartCountIncrement = 1,
                GoodQuantity = 1,
                RejectedQuantity = 0,
            });

    private sealed class InjectedQuantityBoundaryException : Exception
    {
        public InjectedQuantityBoundaryException(FailureBoundary boundary)
            : base($"Injected quantity runtime failure at {boundary}.")
        {
            Boundary = boundary;
        }

        public FailureBoundary Boundary { get; }
    }

    private sealed class FailOnceQuantityReader : IProductionQuantityEvidenceReader
    {
        private readonly IProductionQuantityEvidenceReader _inner;
        private bool _fail;

        public FailOnceQuantityReader(
            IProductionQuantityEvidenceReader inner,
            bool fail)
        {
            _inner = inner;
            _fail = fail;
        }

        public Task<IReadOnlyList<DurableProductionQuantityEvidence>> ReadAsync(
            ObservationStreamId streamId,
            ObservationPosition? afterPosition,
            int batchSize,
            CancellationToken cancellationToken)
        {
            if (_fail)
            {
                _fail = false;
                throw new InjectedQuantityBoundaryException(FailureBoundary.EvidenceRead);
            }

            return _inner.ReadAsync(
                streamId,
                afterPosition,
                batchSize,
                cancellationToken);
        }
    }

    private sealed class FailOnceQuantityStore : IProductionContextProcessingStore
    {
        private readonly IProductionContextProcessingStore _inner;
        private bool _failCheckpointRestore;
        private bool _failCommit;

        public FailOnceQuantityStore(
            IProductionContextProcessingStore inner,
            bool failCheckpointRestore,
            bool failCommit)
        {
            _inner = inner;
            _failCheckpointRestore = failCheckpointRestore;
            _failCommit = failCommit;
        }

        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken)
        {
            if (_failCheckpointRestore)
            {
                _failCheckpointRestore = false;
                throw new InjectedQuantityBoundaryException(FailureBoundary.CheckpointRestore);
            }

            return _inner.ReadCheckpointAsync(
                processorId,
                streamId,
                cancellationToken);
        }

        public Task CommitAsync(
            ProductionContextProcessingCommit commit,
            CancellationToken cancellationToken)
        {
            if (_failCommit)
            {
                _failCommit = false;
                throw new InjectedQuantityBoundaryException(FailureBoundary.Commit);
            }

            return _inner.CommitAsync(commit, cancellationToken);
        }
    }
}
