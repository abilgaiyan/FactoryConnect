using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public enum QualityEvidenceKind { ProductionAdmission = 0, QuantityPopulation = 1, MeasurementCoverage = 2, MeasurementSet = 3, SourceDependency = 4 }
public enum QualityProductionAdmissionState { CompleteAdmitted = 0, EstablishedZero = 1, Incomplete = 2, Unproven = 3, Conflicting = 4 }
public enum QualityMeasurementState { ExactAuthoritative = 0, EstablishedAbsent = 1, Partial = 2, Conflicting = 3, Indeterminate = 4 }

/// <summary>Opaque authority-owned revision. Construction validates structure, not business authority.</summary>
public sealed record QualityEvidenceReference
{
    public QualityEvidenceReference(QualityEvidenceKind kind, string authority, string identity, string revision)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        Kind = kind; Authority = authority; Identity = identity; Revision = revision;
    }
    public QualityEvidenceKind Kind { get; }
    public string Authority { get; }
    public string Identity { get; }
    public string Revision { get; }
}

public sealed record QualityAssessmentScope
{
    public QualityAssessmentScope(MachineId machineId, OperationalMetricPeriodId periodId,
        DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, OperationalMetricEvaluationContextKey context)
    {
        if (machineId.IsEmpty) throw new ArgumentException("Machine is required.", nameof(machineId));
        ArgumentNullException.ThrowIfNull(periodId);
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        if (startsAtUtc.Offset != TimeSpan.Zero || endsAtUtc.Offset != TimeSpan.Zero || endsAtUtc <= startsAtUtc)
            throw new ArgumentException("A positive half-open UTC interval is required.", nameof(endsAtUtc));
        SiteId = periodId switch
        {
            OperationalMetricPeriodId.Shift { ShiftOccurrenceId: { } shift }
                when shift.StartsAtUtc == startsAtUtc && shift.EndsAtUtc == endsAtUtc => shift.SiteId,
            OperationalMetricPeriodId.ProductionDay { ProductionDayId: { } day } => day.SiteId,
            _ => throw new ArgumentException("Period must be valid and shift boundaries must match exactly.", nameof(periodId)),
        };
        MachineId = machineId; PeriodId = periodId; StartsAtUtc = startsAtUtc; EndsAtUtc = endsAtUtc; Context = context;
    }
    public SiteId SiteId { get; }
    public MachineId MachineId { get; }
    public OperationalMetricPeriodId PeriodId { get; }
    public DateTimeOffset StartsAtUtc { get; }
    public DateTimeOffset EndsAtUtc { get; }
    public OperationalMetricEvaluationContextKey Context { get; }
}

public sealed class QualityProductionAdmission
{
    public QualityProductionAdmission(QualityAssessmentScope scope, QualityProductionAdmissionState state,
        QualityEvidenceReference decision, string ruleVersion, QualityEvidenceReference? population,
        decimal? producedQuantity, IEnumerable<QualityEvidenceReference> dependencies)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (decision.Kind != QualityEvidenceKind.ProductionAdmission)
            throw new ArgumentException("Admission decision reference required.", nameof(decision));
        var admitted = state is QualityProductionAdmissionState.CompleteAdmitted or QualityProductionAdmissionState.EstablishedZero;
        if (admitted && (population?.Kind != QualityEvidenceKind.QuantityPopulation || producedQuantity is null ||
            (state == QualityProductionAdmissionState.CompleteAdmitted ? producedQuantity <= 0 : producedQuantity != 0)))
            throw new ArgumentException("CompleteAdmitted requires positive production; EstablishedZero requires zero, with a population reference.", nameof(producedQuantity));
        if (!admitted && (producedQuantity is not null || population is not null))
            throw new ArgumentException("Non-admitted evidence cannot expose admitted quantities or population.", nameof(producedQuantity));
        Scope = scope; State = state; Decision = decision; RuleVersion = ruleVersion;
        Population = population; ProducedQuantity = producedQuantity;
        Dependencies = QualityContractCollections.Evidence(dependencies);
    }
    public QualityAssessmentScope Scope { get; }
    public QualityProductionAdmissionState State { get; }
    public QualityEvidenceReference Decision { get; }
    public string RuleVersion { get; }
    public QualityEvidenceReference? Population { get; }
    public decimal? ProducedQuantity { get; }
    public IReadOnlyList<QualityEvidenceReference> Dependencies { get; }
}

/// <summary>Coverage is supplied by a measurement authority; empty query results must not be converted to EstablishedAbsent.</summary>
public sealed class QualityMeasurementCoverage
{
    public QualityMeasurementCoverage(QualityAssessmentScope scope, QualityMeasurementState state,
        QualityEvidenceReference decision, QualityEvidenceReference? population,
        decimal? producedQuantity, decimal? goodQuantity, decimal? rejectedQuantity,
        IEnumerable<QualityEvidenceReference> evidence)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(decision);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (decision.Kind != QualityEvidenceKind.MeasurementCoverage)
            throw new ArgumentException("Authority-owned measurement coverage reference required.", nameof(decision));
        var items = QualityContractCollections.Evidence(evidence);
        if (state == QualityMeasurementState.ExactAuthoritative)
        {
            if (population?.Kind != QualityEvidenceKind.QuantityPopulation || producedQuantity is null ||
                goodQuantity is null || rejectedQuantity is null || producedQuantity < 0 || goodQuantity < 0 || rejectedQuantity < 0 ||
                goodQuantity > producedQuantity || rejectedQuantity != producedQuantity - goodQuantity ||
                !items.Any(static item => item.Kind == QualityEvidenceKind.MeasurementSet))
                throw new ArgumentException("Exact measured quantities require a population, measurement evidence and nonnegative good + rejected = produced.", nameof(goodQuantity));
        }
        else if (population is not null || producedQuantity is not null || goodQuantity is not null || rejectedQuantity is not null)
            throw new ArgumentException("Non-exact measurement decisions cannot expose complete quantities.", nameof(goodQuantity));
        Scope = scope; State = state; Decision = decision; Population = population;
        ProducedQuantity = producedQuantity; GoodQuantity = goodQuantity; RejectedQuantity = rejectedQuantity; Evidence = items;
    }
    public QualityAssessmentScope Scope { get; }
    public QualityMeasurementState State { get; }
    public QualityEvidenceReference Decision { get; }
    public QualityEvidenceReference? Population { get; }
    public decimal? ProducedQuantity { get; }
    public decimal? GoodQuantity { get; }
    public decimal? RejectedQuantity { get; }
    public IReadOnlyList<QualityEvidenceReference> Evidence { get; }
}

public sealed record QualityPolicyVersion
{
    public QualityPolicyVersion(string policyId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        PolicyId = policyId; Version = version;
    }
    public string PolicyId { get; }
    public string Version { get; }
}

/// <summary>An immutable approval supplied by commissioning; this contract does not grant approval.</summary>
public sealed class ApprovedAllGoodQualityPolicy
{
    public ApprovedAllGoodQualityPolicy(QualityPolicyVersion identity, SiteId siteId, IEnumerable<MachineId> machines,
        string approvalReference, string approvedBy, DateTimeOffset effectiveFromUtc, DateTimeOffset? effectiveToUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        if (siteId.IsEmpty) throw new ArgumentException("Site is required.", nameof(siteId));
        var snapshot = machines.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(static machine => machine.IsEmpty) || snapshot.Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Explicit unique machines are required.", nameof(machines));
        if (effectiveFromUtc.Offset != TimeSpan.Zero || effectiveToUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            effectiveToUtc <= effectiveFromUtc)
            throw new ArgumentException("Policy boundaries must be a positive half-open UTC interval.", nameof(effectiveToUtc));
        Identity = identity; SiteId = siteId; Machines = Array.AsReadOnly(snapshot);
        ApprovalReference = approvalReference; ApprovedBy = approvedBy;
        EffectiveFromUtc = effectiveFromUtc; EffectiveToUtc = effectiveToUtc;
    }
    public QualityPolicyVersion Identity { get; }
    public SiteId SiteId { get; }
    public IReadOnlyList<MachineId> Machines { get; }
    public string ApprovalReference { get; }
    public string ApprovedBy { get; }
    public DateTimeOffset EffectiveFromUtc { get; }
    public DateTimeOffset? EffectiveToUtc { get; }
}

/// <summary>Exact authority-selected target. No revision ordering or authority is inferred here.</summary>
public sealed class QualityAssessmentTarget
{
    public QualityAssessmentTarget(QualityAssessmentScope scope, QualityProductionAdmission production,
        QualityMeasurementCoverage measurement, IEnumerable<ApprovedAllGoodQualityPolicy> policies, string ruleVersion)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);
        if (scope != production.Scope || scope != measurement.Scope)
            throw new ArgumentException("All authority decisions must bind the exact assessed scope.", nameof(scope));
        if (measurement.State == QualityMeasurementState.ExactAuthoritative &&
            (production.Population is null || measurement.Population != production.Population || measurement.ProducedQuantity != production.ProducedQuantity))
            throw new ArgumentException("Measured quantities must refer to the exact admitted production population and total.", nameof(measurement));
        var snapshot = policies.ToArray();
        if (snapshot.Any(static policy => policy is null) || snapshot.Select(static policy => policy.Identity).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Policy versions must be non-null and unique.", nameof(policies));
        Scope = scope; Production = production; Measurement = measurement;
        Policies = Array.AsReadOnly(snapshot); RuleVersion = ruleVersion;
    }
    public QualityAssessmentScope Scope { get; }
    public QualityProductionAdmission Production { get; }
    public QualityMeasurementCoverage Measurement { get; }
    public IReadOnlyList<ApprovedAllGoodQualityPolicy> Policies { get; }
    public string RuleVersion { get; }
}

internal static class QualityContractCollections
{
    internal static ReadOnlyCollection<QualityEvidenceReference> Evidence(IEnumerable<QualityEvidenceReference> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var snapshot = evidence.ToArray();
        if (snapshot.Any(static item => item is null) || snapshot.Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Evidence references must be non-null and unique.", nameof(evidence));
        return Array.AsReadOnly(snapshot);
    }
}
