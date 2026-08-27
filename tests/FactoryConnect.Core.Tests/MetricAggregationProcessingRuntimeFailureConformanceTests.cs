using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class MetricAggregationProcessingRuntimeFailureConformanceTests
{
    private static readonly MachineId Machine = new(new Guid("dddddddd-dddd-dddd-dddd-dddddddddddd"));

    [Fact]
    public async Task ReaderReturningMoreThanRequestedMaximumIsRejectedWithoutCommit()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var reader = new OversizedMetricInputReader(
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

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunCycleAsync());

        Assert.Equal(2, reader.LastRequest?.MaxCount);
        Assert.Null(await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));
        var shiftKey = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var dayKey = new ProductionDayMetricAggregateKey(Machine, day, MetricInputFactKeys.RunningDuration);
        Assert.Null(await store.ReadShiftAggregateAsync(processor, shiftKey, CancellationToken.None));
        Assert.Null(await store.ReadProductionDayAggregateAsync(processor, dayKey, CancellationToken.None));
    }

    [Fact]
    public async Task CheckpointRestoreFailureIsRetriedOnNextCycle()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var innerStore = new InMemoryMetricAggregationStore();
        var store = new FailOnceCheckpointReadStore(innerStore);
        var reader = new CountingEmptyReader(stream);
        var runtime = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            10);

        await Assert.ThrowsAsync<InjectedCheckpointRestoreException>(async () =>
            await runtime.RunCycleAsync());
        Assert.Equal(1, store.ReadCheckpointCallCount);
        Assert.Equal(0, reader.ReadCallCount);

        Assert.Equal(0, await runtime.RunCycleAsync());
        Assert.Equal(2, store.ReadCheckpointCallCount);
        Assert.Equal(1, reader.ReadCallCount);
    }

    [Fact]
    public async Task ReaderFailureDoesNotAdvanceAndRetryUsesSameRestoredPosition()
    {
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var store = new InMemoryMetricAggregationStore();
        var initial = CreateInput(stream, 1, "FACT-1", 10m, occurrence, day, 7);
        var initialCheckpoint = new MetricAggregationCheckpoint(
            processor,
            stream,
            new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, initialCheckpoint, [initial]),
            CancellationToken.None);

        var next = CreateInput(stream, 2, "FACT-2", 20m, occurrence, day, 8);
        var reader = new FailOnceMetricInputReader(stream, next);
        var runtime = new MetricAggregationProcessingRuntime(
            processor,
            reader,
            store,
            stream,
            10);

        await Assert.ThrowsAsync<InjectedMetricInputReadException>(async () =>
            await runtime.RunCycleAsync());
        Assert.Equal(initialCheckpoint, await store.ReadCheckpointAsync(processor, stream, CancellationToken.None));

        Assert.Equal(1, await runtime.RunCycleAsync());
        Assert.Equal(2, reader.Requests.Count);
        Assert.All(reader.Requests, request => Assert.Equal(new MetricInputPosition(1), request.AfterPosition));

        var key = new ShiftMetricAggregateKey(Machine, occurrence, MetricInputFactKeys.RunningDuration);
        var aggregate = await store.ReadShiftAggregateAsync(processor, key, CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(30m, aggregate.Value);
        Assert.Equal(2, aggregate.InputCount);
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

    private sealed class OversizedMetricInputReader : IMetricInputReader
    {
        private readonly MetricInputStreamId _streamId;
        private readonly PositionedMetricInputFact[] _facts;

        public OversizedMetricInputReader(
            MetricInputStreamId streamId,
            IReadOnlyList<PositionedMetricInputFact> facts)
        {
            _streamId = streamId;
            _facts = facts.ToArray();
        }

        public MetricInputReadRequest? LastRequest { get; private set; }

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(new MetricInputReadBatch(
                _streamId,
                request.AfterPosition,
                _facts[^1].Position,
                _facts));
        }
    }

    private sealed class CountingEmptyReader : IMetricInputReader
    {
        private readonly MetricInputStreamId _streamId;

        public CountingEmptyReader(MetricInputStreamId streamId)
        {
            _streamId = streamId;
        }

        public int ReadCallCount { get; private set; }

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            return ValueTask.FromResult(new MetricInputReadBatch(
                _streamId,
                request.AfterPosition,
                request.AfterPosition,
                []));
        }
    }

    private sealed class FailOnceMetricInputReader : IMetricInputReader
    {
        private readonly MetricInputStreamId _streamId;
        private readonly PositionedMetricInputFact _input;
        private bool _fail = true;

        public FailOnceMetricInputReader(
            MetricInputStreamId streamId,
            PositionedMetricInputFact input)
        {
            _streamId = streamId;
            _input = input;
        }

        public List<MetricInputReadRequest> Requests { get; } = [];

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            if (_fail)
            {
                _fail = false;
                throw new InjectedMetricInputReadException();
            }

            return ValueTask.FromResult(new MetricInputReadBatch(
                _streamId,
                request.AfterPosition,
                _input.Position,
                [_input]));
        }
    }

    private sealed class FailOnceCheckpointReadStore : IMetricAggregationStore
    {
        private readonly IMetricAggregationStore _inner;
        private bool _fail = true;

        public FailOnceCheckpointReadStore(IMetricAggregationStore inner)
        {
            _inner = inner;
        }

        public int ReadCheckpointCallCount { get; private set; }

        public ValueTask<MetricAggregationCheckpoint?> ReadCheckpointAsync(
            MetricAggregationProcessorId processorId,
            MetricInputStreamId streamId,
            CancellationToken cancellationToken)
        {
            ReadCheckpointCallCount++;
            if (_fail)
            {
                _fail = false;
                throw new InjectedCheckpointRestoreException();
            }

            return _inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);
        }

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
            CancellationToken cancellationToken) =>
            _inner.CommitAsync(commit, cancellationToken);
    }

    private sealed class InjectedCheckpointRestoreException : Exception
    {
    }

    private sealed class InjectedMetricInputReadException : Exception
    {
    }
}
