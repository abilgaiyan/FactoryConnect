using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class MetricAggregationProcessingRuntimeTests
{
    private static readonly MachineId Machine = new(new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc"));

    [Fact]
    public async Task RuntimeProcessesMultipleBatchesAndRestoresCheckpointOnRestart()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var reader = new TestMetricInputReader(
            stream,
            [
                CreateInput(stream, 1, "FACT-1", 10m, occurrence, day, 7),
                CreateInput(stream, 2, "FACT-2", 20m, occurrence, day, 8),
                CreateInput(stream, 3, "FACT-3", 30m, occurrence, day, 9),
            ]);
        var store = new InMemoryMetricAggregationStore();
        var runtime = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            2);

        Assert.Equal(2, await runtime.RunCycleAsync());
        Assert.Equal(1, await runtime.RunCycleAsync());
        Assert.Equal(0, await runtime.RunCycleAsync());

        var restarted = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            2);
        Assert.Equal(0, await restarted.RunCycleAsync());

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(3, aggregate.InputCount);
        Assert.Equal(new MetricInputPosition(3), reader.LastRequest?.AfterPosition);
    }

    [Fact]
    public async Task FailedCommitDoesNotAcknowledgeAndRetryReadsSameWindow()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var reader = new TestMetricInputReader(
            stream,
            [CreateInput(stream, 1, "FACT-1", 60m, occurrence, day, 7)]);
        var innerStore = new InMemoryMetricAggregationStore();
        var store = new FailOnceAggregationStore(innerStore);
        var runtime = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            10);

        await Assert.ThrowsAsync<InjectedAggregationCommitException>(async () =>
            await runtime.RunCycleAsync());
        Assert.Null(await innerStore.ReadCheckpointAsync(processor, stream, CancellationToken.None));

        Assert.Equal(1, await runtime.RunCycleAsync());
        Assert.Equal(2, reader.Requests.Count);
        Assert.All(reader.Requests, static request => Assert.Null(request.AfterPosition));

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await innerStore.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(1, aggregate.InputCount);
    }

    [Fact]
    public async Task EmptyProgressWindowAdvancesCheckpointWithoutAggregateMutation()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var reader = new EmptyProgressMetricInputReader(stream, new MetricInputPosition(5));
        var store = new InMemoryMetricAggregationStore();
        var runtime = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            10);

        Assert.Equal(0, await runtime.RunCycleAsync());

        var checkpoint = await store.ReadCheckpointAsync(processor, stream, CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(new MetricInputPosition(5), checkpoint.Position);
    }

    [Fact]
    public async Task NoProgressWindowDoesNotCommitCheckpoint()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var reader = new EmptyProgressMetricInputReader(stream, null);
        var store = new InMemoryMetricAggregationStore();
        var runtime = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            10);

        Assert.Equal(0, await runtime.RunCycleAsync());
        Assert.Null(await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReaderWindowMismatchIsRejectedBeforeCommit()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var otherStream = new MetricInputStreamId(Machine, "other");
        var reader = new MismatchedMetricInputReader(otherStream);
        var store = new InMemoryMetricAggregationStore();
        var runtime = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            10);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunCycleAsync());
        Assert.Null(await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
    }

    private static ShiftOccurrenceId CreateOccurrence() =>
        new(
            new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero));

    private static ProductionDayId CreateProductionDay() =>
        new(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));

    private static PositionedMetricInputFact CreateInput(
        MetricInputStreamId stream,
        ulong position,
        string factId,
        decimal value,
        ShiftOccurrenceId occurrence,
        ProductionDayId productionDay,
        int hour)
    {
        var startsAt = new DateTimeOffset(2026, 8, 27, hour, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = MetricInputFactKeys.RunningDuration,
            Value = value,
            Unit = MetricInputFactUnits.Seconds,
            StartsAtUtc = startsAt,
            EndsAtUtc = startsAt.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = Machine,
            ShiftId = occurrence.ShiftId,
            ShiftScheduleAssignmentId = occurrence.ShiftScheduleAssignmentId,
        };

        return new PositionedMetricInputFact(
            stream,
            new MetricInputPosition(position),
            fact,
            occurrence,
            productionDay);
    }

    private sealed class TestMetricInputReader : IMetricInputReader
    {
        private readonly MetricInputStreamId _streamId;
        private readonly PositionedMetricInputFact[] _facts;

        public TestMetricInputReader(
            MetricInputStreamId streamId,
            IReadOnlyList<PositionedMetricInputFact> facts)
        {
            _streamId = streamId;
            _facts = facts.ToArray();
        }

        public List<MetricInputReadRequest> Requests { get; } = [];

        public MetricInputReadRequest? LastRequest => Requests.Count == 0 ? null : Requests[^1];

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            var facts = _facts
                .Where(item => request.AfterPosition is null || item.Position > request.AfterPosition)
                .Take(request.MaxCount)
                .ToArray();
            var through = facts.Length == 0
                ? request.AfterPosition
                : facts[^1].Position;

            return ValueTask.FromResult(new MetricInputReadBatch(
                _streamId,
                request.AfterPosition,
                through,
                facts));
        }
    }

    private sealed class EmptyProgressMetricInputReader : IMetricInputReader
    {
        private readonly MetricInputStreamId _streamId;
        private readonly MetricInputPosition? _throughPosition;

        public EmptyProgressMetricInputReader(
            MetricInputStreamId streamId,
            MetricInputPosition? throughPosition)
        {
            _streamId = streamId;
            _throughPosition = throughPosition;
        }

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MetricInputReadBatch(
                _streamId,
                request.AfterPosition,
                _throughPosition,
                []));
        }
    }

    private sealed class MismatchedMetricInputReader : IMetricInputReader
    {
        private readonly MetricInputStreamId _streamId;

        public MismatchedMetricInputReader(MetricInputStreamId streamId)
        {
            _streamId = streamId;
        }

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MetricInputReadBatch(
                _streamId,
                request.AfterPosition,
                request.AfterPosition,
                []));
        }
    }

    private sealed class FailOnceAggregationStore : IMetricAggregationStore
    {
        private readonly IMetricAggregationStore _inner;
        private bool _fail = true;

        public FailOnceAggregationStore(IMetricAggregationStore inner)
        {
            _inner = inner;
        }

        public ValueTask<MetricAggregationCheckpoint?> ReadCheckpointAsync(
            MetricAggregationProcessorId processorId,
            MetricInputStreamId streamId,
            CancellationToken cancellationToken) =>
            _inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);

        public ValueTask<MetricAggregateValue?> ReadShiftAggregateAsync(
            MetricAggregationProcessorId processorId,
            ShiftMetricAggregateKey key,
            CancellationToken cancellationToken) =>
            _inner.ReadShiftAggregateAsync(processorId, key, cancellationToken);

        public ValueTask<MetricAggregateValue?> ReadProductionDayAggregateAsync(
            MetricAggregationProcessorId processorId,
            ProductionDayMetricAggregateKey key,
            CancellationToken cancellationToken) =>
            _inner.ReadProductionDayAggregateAsync(processorId, key, cancellationToken);

        public ValueTask CommitAsync(
            MetricAggregationCommit commit,
            CancellationToken cancellationToken)
        {
            if (_fail)
            {
                _fail = false;
                throw new InjectedAggregationCommitException();
            }

            return _inner.CommitAsync(commit, cancellationToken);
        }
    }

    private sealed class InjectedAggregationCommitException : Exception
    {
    }
}
