using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class DemoPartCountEvidenceReaderTests
{
    [Fact]
    public async Task PositiveCounterDeltaProducesSyntheticGoodQuantity()
    {
        var fixture = CreateFixture(
            CreatePartCount(1, 7, 10),
            CreatePartCount(2, 7, 11));

        var result = await fixture.Reader.ReadAsync(
            fixture.QuantityStreamId, null, 10, CancellationToken.None);

        var actual = Assert.Single(result);
        Assert.Equal(new ObservationPosition(2), actual.Position);
        Assert.Equal(1, actual.Evidence.PartCountIncrement);
        Assert.Equal(1, actual.Evidence.GoodQuantity);
        Assert.Null(actual.Evidence.RejectedQuantity);
    }

    [Fact]
    public async Task UnchangedOrDecreasedCounterProducesNoEvidence()
    {
        var fixture = CreateFixture(
            CreatePartCount(1, 7, 10),
            CreatePartCount(2, 7, 10),
            CreatePartCount(3, 7, 9));

        var result = await fixture.Reader.ReadAsync(
            fixture.QuantityStreamId, null, 10, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task InstanceChangeDoesNotCreateCrossInstanceIncrement()
    {
        var fixture = CreateFixture(
            CreatePartCount(1, 7, 10),
            CreatePartCount(2, 8, 25),
            CreatePartCount(3, 8, 26));

        var result = await fixture.Reader.ReadAsync(
            fixture.QuantityStreamId, null, 10, CancellationToken.None);

        var actual = Assert.Single(result);
        Assert.Equal(new ObservationPosition(3), actual.Position);
        Assert.Equal(1, actual.Evidence.PartCountIncrement);
        Assert.Equal(1, actual.Evidence.GoodQuantity);
    }

    [Fact]
    public async Task RestartReconstructsPredecessorBeforePersistedCheckpoint()
    {
        var fixture = CreateFixture(
            CreatePartCount(1, 7, 10),
            CreatePartCount(2, 7, 11),
            CreatePartCount(3, 7, 13));

        var first = await fixture.Reader.ReadAsync(
            fixture.QuantityStreamId, null, 1, CancellationToken.None);
        var firstEvidence = Assert.Single(first);
        Assert.Equal(new ObservationPosition(2), firstEvidence.Position);
        Assert.Equal(1, firstEvidence.Evidence.PartCountIncrement);

        var restarted = CreateFixture(fixture.RawObservations);
        var resumed = await restarted.Reader.ReadAsync(
            restarted.QuantityStreamId, firstEvidence.Position, 10, CancellationToken.None);

        var resumedEvidence = Assert.Single(resumed);
        Assert.Equal(new ObservationPosition(3), resumedEvidence.Position);
        Assert.Equal(2, resumedEvidence.Evidence.PartCountIncrement);
        Assert.Equal(2, resumedEvidence.Evidence.GoodQuantity);
    }

    private static Fixture CreateFixture(params DurableMachineObservation[] observations) =>
        CreateFixture((IReadOnlyList<DurableMachineObservation>)observations);

    private static Fixture CreateFixture(IReadOnlyList<DurableMachineObservation> observations)
    {
        var machineId = observations[0].StreamId.MachineId;
        var rawStreamId = observations[0].StreamId;
        var quantityStreamId = new ObservationStreamId(machineId, "part_count");
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var scope = new ProductionContextProcessingScope
        {
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            StreamId = rawStreamId,
        };
        var context = new ProductionContextAssignment
        {
            Id = new ProductionContextAssignmentId("CTX-1"),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            EffectiveFrom = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero),
            EffectiveTo = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
        };
        var schedule = new ShiftScheduleAssignment
        {
            Id = new ShiftScheduleAssignmentId("SCHEDULE-1"),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            TimeZoneId = new FactoryTimeZoneId("UTC"),
            ShiftId = new ShiftId("SHIFT-1"),
            Name = "Day",
            StartsAtLocal = new TimeOnly(0, 0),
            EndsAtLocal = new TimeOnly(23, 59),
            EffectiveFrom = new DateOnly(2026, 9, 1),
        };
        var reader = new DemoPartCountEvidenceReader(
            new ObservationReader(observations),
            new ContextReader(context),
            new ShiftOccurrenceResolver(new ShiftReader(schedule)),
            new Dictionary<MachineId, ProductionContextProcessingScope> { [machineId] = scope });

        return new Fixture(reader, quantityStreamId, observations);
    }

    private static DurableMachineObservation CreatePartCount(
        ulong position,
        ulong instanceId,
        decimal count)
    {
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var streamId = new ObservationStreamId(machineId, "mtconnect:CNC-01");
        return new DurableMachineObservation(
            new ObservationPosition(position),
            streamId,
            instanceId,
            position,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "mtconnect",
                Address = "part_count",
                Type = SignalType.Numeric,
                Value = count,
                Quality = ObservationQuality.Good,
                Timestamp = new DateTimeOffset(2026, 9, 25, 12, (int)position, 0, TimeSpan.Zero),
            });
    }

    private sealed record Fixture(
        DemoPartCountEvidenceReader Reader,
        ObservationStreamId QuantityStreamId,
        IReadOnlyList<DurableMachineObservation> RawObservations);

    private sealed class ObservationReader(IReadOnlyList<DurableMachineObservation> observations)
        : IDurableObservationReader
    {
        public ValueTask<ObservationReadBatch> ReadAsync(
            ObservationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            var selected = observations
                .Where(item => request.AfterPosition is null || item.Position > request.AfterPosition)
                .Take(request.BatchSize)
                .ToArray();
            var hasMore = observations.Any(item =>
                (request.AfterPosition is null || item.Position > request.AfterPosition) &&
                !selected.Contains(item));
            return ValueTask.FromResult(new ObservationReadBatch(request.StreamId, selected, hasMore));
        }
    }

    private sealed class ContextReader(ProductionContextAssignment assignment) : IProductionContextReader
    {
        public Task<IReadOnlyList<ProductionContextAssignment>> ReadAsync(
            MachineId machineId,
            DateTimeOffset effectiveFrom,
            DateTimeOffset effectiveTo,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProductionContextAssignment>>([assignment]);
    }

    private sealed class ShiftReader(ShiftScheduleAssignment assignment) : IShiftScheduleReader
    {
        public Task<IReadOnlyList<ShiftScheduleAssignment>> ReadAssignmentsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShiftScheduleAssignment>>([assignment]);

        public Task<IReadOnlyList<ShiftCalendarOverride>> ReadExceptionsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShiftCalendarOverride>>([]);
    }
}
