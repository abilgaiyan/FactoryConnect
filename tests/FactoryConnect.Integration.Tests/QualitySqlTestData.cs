using FactoryConnect.Abstractions;
using FactoryConnect.Core.Quality;

namespace FactoryConnect.Integration.Tests;

internal static class QualitySqlTestData
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 15, 0, 30, 0, TimeSpan.Zero);
    internal static readonly MachineId Machine = new(new Guid("11111111-2222-3333-4444-555555555555"));
    internal static QualityEvidenceReference Reference(QualityEvidenceKind kind, string id, string revision = "1") => new(kind, "authority", id, revision);
    internal static ApprovedAllGoodQualityPolicy Policy(string version = "1", string approval = "approval") =>
        new(new QualityPolicyVersion("policy", version), new SiteId("site"), [Machine], approval, "approver", Start, null);
    internal static QualityAssessmentTarget Target(string productionRevision = "1", string policyVersion = "1", decimal produced = 100,
        bool measured = false, bool day = false, QualityMeasurementState measurementState = QualityMeasurementState.EstablishedAbsent)
    {
        OperationalMetricPeriodId period = day
            ? new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(new SiteId("site"), new DateOnly(2026, 10, 15)))
            : new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(new SiteId("site"), new ShiftScheduleAssignmentId("schedule"), new ShiftId("shift"), Start, Start.AddHours(8)));
        var scope = new QualityAssessmentScope(Machine, period, Start, Start.AddHours(8),
            new OperationalMetricEvaluationContextKey { PartId = new PartId("part"), OperationId = new OperationId("operation") });
        var population = Reference(QualityEvidenceKind.QuantityPopulation, "population", productionRevision);
        var production = new QualityProductionAdmission(scope,
            produced == 0 ? QualityProductionAdmissionState.EstablishedZero : QualityProductionAdmissionState.CompleteAdmitted,
            Reference(QualityEvidenceKind.ProductionAdmission, "admission", productionRevision), "admission-v1", population, produced,
            [Reference(QualityEvidenceKind.SourceDependency, "source", productionRevision)]);
        var measurement = measured
            ? new QualityMeasurementCoverage(scope, QualityMeasurementState.ExactAuthoritative,
                Reference(QualityEvidenceKind.MeasurementCoverage, "coverage", "measured"), population, produced, produced * 0.9m, produced * 0.1m,
                [Reference(QualityEvidenceKind.MeasurementSet, "measurement")])
            : new QualityMeasurementCoverage(scope, measurementState, Reference(QualityEvidenceKind.MeasurementCoverage, "coverage"), null, null, null, null, []);
        return new QualityAssessmentTarget(scope, production, measurement, [Policy(policyVersion)], QualityAssessor.RuleVersion);
    }
}
