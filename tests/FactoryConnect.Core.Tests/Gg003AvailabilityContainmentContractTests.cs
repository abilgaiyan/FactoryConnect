using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

// Characterization tests for the evidence-gap branch, not an implemented containment.
// Uses sanitized exact offline evidence; identity aliases are not deployment IDs.
// Never opens or replays the RCA database.
public sealed partial class Gg003AvailabilityContainmentContractTests
{
    private static readonly MachineId Machine = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly SiteId Site = new("rca-site");
    private static readonly MetricAggregationProcessorId Processor = new($"metric-aggregation:{Machine.Value}");
    private static readonly MetricInputStreamId Stream = MetricInputStreamId.ForMachine(Machine);
    private static readonly MetricAggregationCheckpoint Revision = new(Processor, Stream, new MetricInputPosition(534));
    private static readonly ShiftOccurrenceId Shift = new(Site,
        new ShiftScheduleAssignmentId("rca-shift-1-schedule"), new ShiftId("SHIFT-1"),
        DateTimeOffset.Parse("2026-10-16T00:30:00Z", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-10-16T08:30:00Z", CultureInfo.InvariantCulture));
    private static readonly ProductionDayId Day = new(Site, new DateOnly(2026, 10, 16));

    [Fact]
    public void SanitizedFixtureHasExactSourceCutOrderingAndContributionProvenance()
    {
        using var manifestStream = typeof(Gg003AvailabilityContainmentContractTests).Assembly.GetManifestResourceStream(
            "FactoryConnect.Core.Tests.TestData.gg003-revision-534-source-cut.json")!;
        using var manifest = JsonDocument.Parse(manifestStream);
        var cut = manifest.RootElement.GetProperty("sourceCut");
        Assert.Equal(Revision.Position.Value, cut.GetProperty("position").GetUInt64());
        Assert.Equal(Processor.Value, cut.GetProperty("processorId").GetString());
        Assert.Equal(Machine.Value, cut.GetProperty("machineId").GetGuid());
        Assert.Equal(Stream.StreamKey, cut.GetProperty("streamKey").GetString());
        using var fixtureStream = typeof(Gg003AvailabilityContainmentContractTests).Assembly.GetManifestResourceStream(
            "FactoryConnect.Core.Tests.TestData.gg003-revision-534-components.json")!;
        using var fixtureTextReader = new StreamReader(fixtureStream);
        var canonicalText = fixtureTextReader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));
        Assert.Equal(manifest.RootElement.GetProperty("fixtureSha256").GetString(), hash);
        var inputs = LoadInputs();
        Assert.Equal(34, inputs.Length);
        Assert.Equal(inputs.OrderBy(x => x.Position.Value).Select(x => x.Position), inputs.Select(x => x.Position));
        Assert.Equal(34, inputs.Select(x => x.Position).Distinct().Count());
        Assert.Equal(34, inputs.Select(x => x.Fact.Id).Distinct().Count());
        Assert.Equal(new ulong[] { 527, 530, 532 }, inputs
            .Where(x => x.Fact.Key == MetricInputFactKeys.PlannedProductionDuration).Select(x => x.Position.Value));
        Assert.All(inputs, input =>
        {
            Assert.True(input.Position.Value <= Revision.Position.Value);
            Assert.Equal(input.Fact.Value, (decimal)(input.Fact.EndsAtUtc - input.Fact.StartsAtUtc).Ticks / TimeSpan.TicksPerSecond);
            Assert.InRange(input.Fact.StartsAtUtc, Shift.StartsAtUtc, Shift.EndsAtUtc);
            Assert.InRange(input.Fact.EndsAtUtc, Shift.StartsAtUtc, Shift.EndsAtUtc);
            Assert.NotNull(input.Fact.SourceEligibilityIntervalId);
            Assert.NotNull(input.Fact.SourceContextualizedActivityIntervalId);
            Assert.Equal(Shift, input.ShiftOccurrenceId);
            Assert.Equal(Day, input.ProductionDayId);
        });
        using var fixtureInput = ReadFixture();
        using var fixture = JsonDocument.Parse(fixtureInput);
        foreach (var row in fixture.RootElement.EnumerateArray())
        {
            Assert.Equal(Machine.Value, row.GetProperty("MachineId").GetGuid());
            Assert.Equal(Site.Value, row.GetProperty("SiteId").GetString());
            Assert.Equal(Site.Value, row.GetProperty("OccurrenceSiteId").GetString());
            Assert.Equal(Site.Value, row.GetProperty("ProductionDaySiteId").GetString());
            Assert.Equal(Shift.ShiftScheduleAssignmentId.Value, row.GetProperty("OccurrenceShiftScheduleAssignmentId").GetString());
            Assert.Equal(Shift.ShiftScheduleAssignmentId.Value, row.GetProperty("ShiftScheduleAssignmentId").GetString());
            Assert.Equal(Shift.ShiftId.Value, row.GetProperty("ShiftId").GetString());
            Assert.Equal(Shift.ShiftId.Value, row.GetProperty("OccurrenceShiftId").GetString());
            Assert.Equal(Shift.StartsAtUtc, row.GetProperty("OccurrenceStartsAtUtc").GetDateTimeOffset());
            Assert.Equal(Shift.EndsAtUtc, row.GetProperty("OccurrenceEndsAtUtc").GetDateTimeOffset());
            Assert.Equal("2026-10-16", row.GetProperty("ProductionBusinessDate").GetString());
        }
    }

    private static Stream ReadFixture() => typeof(Gg003AvailabilityContainmentContractTests).Assembly.GetManifestResourceStream(
        "FactoryConnect.Core.Tests.TestData.gg003-revision-534-components.json")!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact534SnapshotRetainsDomainFailureWithoutTypedEligibility(bool shift)
    {
        var inputs = LoadInputs();
        var running = inputs.Where(x => x.Fact.Key == MetricInputFactKeys.RunningDuration).ToArray();
        var planned = inputs.Where(x => x.Fact.Key == MetricInputFactKeys.PlannedProductionDuration).ToArray();
        Assert.Equal(31, running.Length);
        Assert.Equal(3, planned.Length);
        Assert.All(running, x => Assert.Equal(false, x.Fact.IsPlannedProductionTime));
        Assert.All(planned, x => Assert.Equal(true, x.Fact.IsPlannedProductionTime));
        Assert.True(running.Max(x => x.Fact.EndsAtUtc) < planned.Min(x => x.Fact.StartsAtUtc));
        var fixture = Create(shift, inputs);
        var request = new OperationalMetricComponentSnapshotRequest(fixture.Key, Processor,
            BuiltInOperationalMetricDefinitions.All.Single(x => x.Id == fixture.Key.DefinitionId).Operands);
        var snapshot = await fixture.Reader.ReadAsync(request, CancellationToken.None);
        Assert.Equal(Revision, snapshot.Revision);
        var actual = Assert.Single(snapshot.Components, x => x.SourceIdentity.ComponentKey == MetricInputFactKeys.RunningDuration);
        var denominator = Assert.Single(snapshot.Components, x => x.SourceIdentity.ComponentKey == MetricInputFactKeys.PlannedProductionDuration);
        Assert.Equal(2683.614157m, actual.Aggregate.Value);
        Assert.Equal(2452.013355m, denominator.Aggregate.Value);
        Assert.Equal(31, actual.Aggregate.InputCount);
        Assert.Equal(3, denominator.Aggregate.InputCount);
        Assert.Equal(1.0944533199738628666645251612m, actual.Aggregate.Value / denominator.Aggregate.Value);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Evaluator.EvaluateAsync(fixture.Key, CancellationToken.None));
        Assert.Contains("outside its validated domain constraints", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentEligibilityCanProduceIdenticalAggregateEvidence(bool shift)
    {
        var original = LoadInputs();
        // Counterfactual contract probe, not factory evidence. Change running
        // fragments' eligibility while preserving their durations and identities.
        var changed = original.Select(x => x.Fact.Key == MetricInputFactKeys.RunningDuration
            ? new PositionedMetricInputFact(x.StreamId, x.Position, x.Fact with
            {
                IsPlannedProductionTime = true,
                PlannedProductionScheduleAssignmentId = new PlannedProductionScheduleAssignmentId("probe"),
            }, x.ShiftOccurrenceId, x.ProductionDayId)
            : x).ToArray();
        var first = MetricInputContributionAggregator.Aggregate(Stream, original);
        var second = MetricInputContributionAggregator.Aggregate(Stream, changed);
        if (shift)
            Assert.Equal(first.ShiftContributions, second.ShiftContributions);
        else
            Assert.Equal(first.ProductionDayContributions, second.ProductionDayContributions);
    }

    [Theory]
    [InlineData(false, 0, 60, 0)]
    [InlineData(true, 0, 60, 0)]
    [InlineData(false, 30, 60, 0.5)]
    [InlineData(true, 30, 60, 0.5)]
    [InlineData(false, 60, 60, 1)]
    [InlineData(true, 60, 60, 1)]
    public async Task ValidAggregateOnlyAvailabilityRemainsCalculated(bool shift, int actual, int planned, double expected)
    {
        var fixture = Create(shift, LoadInputs());
        fixture.Reader.Override(MetricInputKeys.ActualProductionTime, actual);
        fixture.Reader.Override(MetricInputKeys.PlannedOperatingTime, planned);
        var result = await fixture.Evaluator.EvaluateAsync(fixture.Key, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.Status);
        Assert.Equal((decimal)expected, result.Value);
        Assert.Null(result.ReasonCode);
        Assert.All(result.OperandEvidence, x => Assert.Equal(Revision, x.SourceRevision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroDenominatorPropagatesUnavailableThroughOeeWithOperandProvenance(bool shift)
    {
        var fixture = Create(shift, LoadInputs());
        fixture.Reader.Override(MetricInputKeys.PlannedOperatingTime, 0m);
        fixture.Reader.Override(MetricInputKeys.ProductionReferenceTime, 1m);
        fixture.Reader.Override(MetricInputKeys.GoodQuantity, 1m);
        fixture.Reader.Override(MetricInputKeys.ProducedQuantity, 1m);
        var oeeKey = new OperationalMetricEvaluationKey(Machine, fixture.Key.PeriodId,
            BuiltInOperationalMetricDefinitions.OeeId, OperationalMetricEvaluationContextKey.Unpartitioned);
        var result = await fixture.Evaluator.EvaluateAsync(oeeKey, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(OperationalMetricEvaluationReasonCode.DependencyUnavailable, result.ReasonCode);
        Assert.Equal("Availability", result.ReasonOperandName);
        var availability = result.DependencyEvidence.Single(x => x.OperandName == "Availability").Evaluation;
        Assert.Equal(OperationalMetricEvaluationReasonCode.ZeroDenominator, availability.ReasonCode);
        Assert.Null(availability.Value);
        Assert.Equal(2683.614157m, availability.OperandEvidence[0].Value);
        Assert.Equal(31, availability.OperandEvidence[0].InputCount);
        Assert.Equal(fixture.Key.PeriodId, availability.OperandEvidence[0].SourceIdentity.PeriodId);
        Assert.All(availability.OperandEvidence, x => Assert.Equal(Revision, x.SourceRevision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOperandRemainsInsufficientEvidence(bool shift)
    {
        var fixture = Create(shift, LoadInputs());
        fixture.Reader.Remove(MetricInputKeys.PlannedOperatingTime);
        var result = await fixture.Evaluator.EvaluateAsync(fixture.Key, CancellationToken.None);
        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, result.ReasonCode);
        Assert.Null(result.Value);
        Assert.Equal(2683.614157m, Assert.Single(result.OperandEvidence).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedQualityDomainViolationIsNotContained(bool shift)
    {
        var fixture = Create(shift, LoadInputs());
        fixture.Reader.Override(MetricInputKeys.GoodQuantity, 2m);
        fixture.Reader.Override(MetricInputKeys.ProducedQuantity, 1m);
        var qualityKey = new OperationalMetricEvaluationKey(Machine, fixture.Key.PeriodId,
            BuiltInOperationalMetricDefinitions.QualityId, OperationalMetricEvaluationContextKey.Unpartitioned);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Evaluator.EvaluateAsync(qualityKey, CancellationToken.None));
    }

    private static (OperationalMetricEvaluationKey Key, SnapshotReader Reader, OperationalMetricEvaluator Evaluator)
        Create(bool shift, IReadOnlyList<PositionedMetricInputFact> inputs)
    {
        OperationalMetricPeriodId period = shift ? new OperationalMetricPeriodId.Shift(Shift)
            : new OperationalMetricPeriodId.ProductionDay(Day);
        var key = new OperationalMetricEvaluationKey(Machine, period, BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var reader = new SnapshotReader(shift, MetricInputContributionAggregator.Aggregate(Stream, inputs));
        var evaluator = new OperationalMetricEvaluator(
            new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All), reader, Processor);
        return (key, reader, evaluator);
    }

    private static PositionedMetricInputFact[] LoadInputs()
    {
        using var stream = typeof(Gg003AvailabilityContainmentContractTests).Assembly.GetManifestResourceStream(
            "FactoryConnect.Core.Tests.TestData.gg003-revision-534-components.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().Select(x => new PositionedMetricInputFact(Stream,
            new MetricInputPosition(x.GetProperty("Position").GetUInt64()), new DurableMetricInputFact
            {
                Id = new MetricInputFactId(x.GetProperty("FactId").GetString()!),
                Key = x.GetProperty("MetricInputKey").GetString()!,
                Value = decimal.Parse(x.GetProperty("MetricValue").GetString()!, CultureInfo.InvariantCulture),
                Unit = x.GetProperty("Unit").GetString()!,
                StartsAtUtc = x.GetProperty("StartsAtUtc").GetDateTimeOffset(),
                EndsAtUtc = x.GetProperty("EndsAtUtc").GetDateTimeOffset(),
                CompanyId = new CompanyId(x.GetProperty("CompanyId").GetString()!),
                SiteId = Site,
                ProductionLineId = new ProductionLineId(x.GetProperty("ProductionLineId").GetString()!),
                ProductionContextAssignmentId = new ProductionContextAssignmentId(x.GetProperty("ProductionContextAssignmentId").GetString()!),
                MachineId = Machine,
                ShiftId = Shift.ShiftId,
                ShiftScheduleAssignmentId = Shift.ShiftScheduleAssignmentId,
                IsPlannedProductionTime = x.GetProperty("IsPlannedProductionTime").GetBoolean(),
                PlannedProductionScheduleAssignmentId = x.TryGetProperty("PlannedProductionScheduleAssignmentId", out var planned)
                    ? new PlannedProductionScheduleAssignmentId(planned.GetString()!) : null,
                SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId(
                    x.GetProperty("SourceContextualizedActivityIntervalId").GetString()!),
                SourceEligibilityIntervalId = new ProductionTimeEligibilityIntervalId(
                    x.GetProperty("SourceEligibilityIntervalId").GetString()!),
            }, Shift, Day)).ToArray();
    }

    private sealed class SnapshotReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly Dictionary<string, MetricAggregateValue> _aggregates;
        public SnapshotReader(bool shift, MetricAggregateContributionSet contributions)
        {
            _aggregates = shift
                ? contributions.ShiftContributions.ToDictionary(x => x.Key.MetricInputKey, x => x.Value)
                : contributions.ProductionDayContributions.ToDictionary(x => x.Key.MetricInputKey, x => x.Value);
        }
        public void Override(string key, decimal value) => _aggregates[OperationalMetricComponentKeyMapping.AggregateKey(key)] =
            new MetricAggregateValue(value, key is MetricInputKeys.GoodQuantity or MetricInputKeys.ProducedQuantity
                ? MetricInputFactUnits.Count : MetricInputFactUnits.Seconds, 1, Shift.StartsAtUtc, Shift.EndsAtUtc);
        public void Remove(string key) => _aggregates.Remove(OperationalMetricComponentKeyMapping.AggregateKey(key));
        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request, CancellationToken cancellationToken)
        {
            var components = request.Operands.Select(operand => (Operand: operand,
                    Key: OperationalMetricComponentKeyMapping.AggregateKey(((OperationalMetricOperandSource.Component)operand.Source).ComponentKey)))
                .Where(x => _aggregates.ContainsKey(x.Key))
                .Select(x => new OperationalMetricComponent(x.Operand.OperandName,
                    new OperationalMetricAggregateSourceIdentity(Processor, Machine, request.EvaluationKey.PeriodId, x.Key),
                    x.Operand.RequiredDimension, _aggregates[x.Key]));
            return ValueTask.FromResult(new OperationalMetricComponentSnapshot(request.EvaluationKey, Revision, components));
        }
    }
}
