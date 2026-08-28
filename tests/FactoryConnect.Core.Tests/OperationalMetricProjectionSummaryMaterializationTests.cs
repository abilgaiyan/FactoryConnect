using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricProjectionSummaryMaterializationTests
{
    [Fact]
    public async Task ProviderCanMaterializePeriodSummaryFromScalarFieldsOnly()
    {
        var machineId = MachineId.New();
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var periodId = new OperationalMetricPeriodId.ProductionDay(day);
        var sourceRevision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregate-m01"),
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(12));
        var projectionProcessorId = new OperationalMetricProjectionProcessorId("projection-m01");
        var key = new OperationalMetricEvaluationKey(
            machineId,
            periodId,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var scalarSummary = new OperationalMetricProjectionSummary(
            projectionProcessorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.75m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            sourceRevision);
        var queryReader = new ScalarOnlySummaryReader([scalarSummary]);
        var reader = new OperationalMetricReportReader(queryReader);

        var report = await reader.ReadProductionDayAsync(
            projectionProcessorId,
            machineId,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(sourceRevision, report.SourceRevision);
        var metric = Assert.Single(report.Metrics);
        Assert.Equal(BuiltInOperationalMetricDefinitions.AvailabilityId, metric.DefinitionId);
        Assert.Equal(0.75m, metric.Value);
        Assert.Equal(1, queryReader.SummaryReadCount);
        Assert.Equal(0, queryReader.DetailReadCount);
    }

    [Fact]
    public void ScalarSummaryRejectsSourceRevisionFromAnotherMachine()
    {
        var machineId = MachineId.New();
        var otherMachineId = MachineId.New();
        var key = EvaluationKey(machineId);
        var wrongRevision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregate-m01"),
            MetricInputStreamId.ForMachine(otherMachineId),
            new MetricInputPosition(1));

        Assert.Throws<ArgumentException>(() => new OperationalMetricProjectionSummary(
            new OperationalMetricProjectionProcessorId("projection-m01"),
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            wrongRevision));
    }

    [Fact]
    public void ScalarSummaryEnforcesStatusValueAndReasonInvariant()
    {
        var machineId = MachineId.New();
        var key = EvaluationKey(machineId);
        var revision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregate-m01"),
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(1));
        var processorId = new OperationalMetricProjectionProcessorId("projection-m01");

        Assert.Throws<ArgumentException>(() => new OperationalMetricProjectionSummary(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            null,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision));

        Assert.Throws<ArgumentException>(() => new OperationalMetricProjectionSummary(
            processorId,
            key,
            OperationalMetricEvaluationStatus.InsufficientEvidence,
            0.5m,
            OperationalMetricUnits.Ratio,
            OperationalMetricEvaluationReasonCode.MissingOperand,
            "ActualProductionTime",
            revision));

        Assert.Throws<ArgumentException>(() => new OperationalMetricProjectionSummary(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Unavailable,
            null,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision));
    }

    private static OperationalMetricEvaluationKey EvaluationKey(MachineId machineId)
    {
        var day = new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29));
        return new OperationalMetricEvaluationKey(
            machineId,
            new OperationalMetricPeriodId.ProductionDay(day),
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private sealed class ScalarOnlySummaryReader : IOperationalMetricProjectionQueryReader
    {
        private readonly IReadOnlyList<OperationalMetricProjectionSummary> _summaries;

        public ScalarOnlySummaryReader(IReadOnlyList<OperationalMetricProjectionSummary> summaries)
        {
            _summaries = summaries;
        }

        public int SummaryReadCount { get; private set; }

        public int DetailReadCount { get; private set; }

        public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadPeriodSummariesAsync(
            OperationalMetricProjectionProcessorId processorId,
            MachineId machineId,
            OperationalMetricPeriodId periodId,
            OperationalMetricEvaluationContextKey contextKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SummaryReadCount++;
            return ValueTask.FromResult(_summaries);
        }

        public ValueTask<OperationalMetricProjection?> ReadDetailAsync(
            OperationalMetricProjectionProcessorId processorId,
            OperationalMetricEvaluationKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetailReadCount++;
            return ValueTask.FromResult<OperationalMetricProjection?>(null);
        }
    }
}
