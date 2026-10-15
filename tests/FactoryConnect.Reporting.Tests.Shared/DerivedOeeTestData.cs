using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Core.Oee;
using FactoryConnect.Core.Quality;

namespace FactoryConnect.Reporting.Tests.Shared;

internal static class DerivedOeeTestData
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 15, 0, 30, 0, TimeSpan.Zero);
    internal static readonly MachineId Machine = new(new Guid("11111111-2222-3333-4444-555555555555"));
    internal static readonly OperationalMetricProjectionProcessorId Processor = new("canonical-projection");
    internal static QualityEvidenceReference Ref(QualityEvidenceKind kind, string id, string revision = "1") => new(kind, "authority", id, revision);
    internal static QualityAssessment Quality(bool measured = false, bool unavailable = false, string revision = "1", bool day = false, decimal good = 90)
    {
        OperationalMetricPeriodId period = day
            ? new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(new SiteId("site"), new DateOnly(2026, 10, 15)))
            : new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(new SiteId("site"), new ShiftScheduleAssignmentId("schedule"), new ShiftId("shift"), Start, Start.AddHours(8)));
        var scope = new QualityAssessmentScope(Machine, period, Start, Start.AddHours(8), OperationalMetricEvaluationContextKey.Unpartitioned);
        var population = Ref(QualityEvidenceKind.QuantityPopulation, "population", revision);
        var production = new QualityProductionAdmission(scope, QualityProductionAdmissionState.CompleteAdmitted,
            Ref(QualityEvidenceKind.ProductionAdmission, "admission", revision), "1", population, 100, []);
        var measurement = new QualityMeasurementCoverage(scope,
            measured ? QualityMeasurementState.ExactAuthoritative : unavailable ? QualityMeasurementState.Indeterminate : QualityMeasurementState.EstablishedAbsent,
            Ref(QualityEvidenceKind.MeasurementCoverage, "measurement", measured ? "measured" : "absent"), measured ? population : null,
            measured ? 100 : null, measured ? good : null, measured ? 100 - good : null,
            measured ? [Ref(QualityEvidenceKind.MeasurementSet, "measured")] : []);
        var policy = new ApprovedAllGoodQualityPolicy(new("policy", "1"), new SiteId("site"), [Machine], "approval-reference", "approver", Start, null);
        return QualityAssessor.Assess(new(scope, production, measurement, [policy], QualityAssessor.RuleVersion));
    }
    internal static MetricAggregationCheckpoint Revision(ulong revision = 1) => new(new MetricAggregationProcessorId("aggregation"), MetricInputStreamId.ForMachine(Machine), new(revision));
    internal static DerivedOeeMetricDependency Metric(QualityAssessmentScope scope, bool availability, ulong revision = 1,
        decimal actual = 80, decimal planned = 100, decimal ideal = 60, OperationalMetricEvaluationStatus status = OperationalMetricEvaluationStatus.Calculated,
        OperationalMetricEvaluationReasonCode? reason = null)
    {
        var checkpoint = Revision(revision);
        var definition = availability ? BuiltInOperationalMetricDefinitions.AvailabilityId : BuiltInOperationalMetricDefinitions.PerformanceId;
        OperationalMetricComponentProjectionEvidence Evidence(string name, string key, decimal value) => new(name,
            new OperationalMetricAggregateSourceIdentity(checkpoint.ProcessorId, scope.MachineId, scope.PeriodId, key), checkpoint,
            MetricDimension.Duration, value, MetricInputFactUnits.Seconds, 1, Start, Start.AddHours(8));
        var evidence = new[] { Evidence("ActualProductionTime", MetricInputFactKeys.RunningDuration, actual),
            availability ? Evidence("PlannedOperatingTime", MetricInputFactKeys.PlannedProductionDuration, planned)
                : Evidence("IdealProductionDuration", MetricInputKeys.ProductionReferenceTime, ideal) };
        var projection = new OperationalMetricProjection(Processor,
            new OperationalMetricEvaluationKey(scope.MachineId, scope.PeriodId, definition, scope.Context), status,
            status == OperationalMetricEvaluationStatus.Calculated ? decimal.Round(availability ? actual / planned : ideal / actual, 8, MidpointRounding.ToEven) : null,
            OperationalMetricUnits.Ratio, status == OperationalMetricEvaluationStatus.Calculated ? null : reason ?? OperationalMetricEvaluationReasonCode.MissingOperand,
            status == OperationalMetricEvaluationStatus.Calculated ? null : "missing-input", checkpoint, evidence);
        return new(scope, projection);
    }
    internal static DerivedOeeTarget Target(QualityAssessment? quality = null, ulong metricRevision = 1, string cutRevision = "1",
        DerivedOeeCutState state = DerivedOeeCutState.Established, decimal ideal = 60, decimal actual = 80, decimal planned = 100,
        OperationalMetricEvaluationStatus availabilityStatus = OperationalMetricEvaluationStatus.Calculated,
        OperationalMetricEvaluationStatus performanceStatus = OperationalMetricEvaluationStatus.Calculated, string rule = DerivedOeeAssessor.RuleVersion)
    {
        quality ??= Quality();
        var a = Metric(quality.Target.Scope, true, metricRevision, actual, planned, ideal, availabilityStatus);
        var p = Metric(quality.Target.Scope, false, metricRevision, actual, planned, ideal, performanceStatus, OperationalMetricEvaluationReasonCode.MissingReferenceTime);
        var cut = new DerivedOeeEvidenceCut(quality.Target.Scope, Ref(QualityEvidenceKind.SourceDependency, "coherence", cutRevision), state,
            Processor, Revision(metricRevision), quality.Identity, quality.ContentFingerprint);
        return new(cut, a, p, quality, rule);
    }
}
