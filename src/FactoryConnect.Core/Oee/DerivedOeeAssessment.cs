using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Core.Quality;

namespace FactoryConnect.Core.Oee;

public enum DerivedOeeCutState { Established = 0, Unproven = 1, Conflicting = 2 }
public enum DerivedOeeReason { Calculated, CoherenceUnproven, CoherenceConflicting, DependenciesUnavailable, ProductOutOfRange }

/// <summary>An authority-supplied cross-lineage binding; construction validates structure, not authority.</summary>
public sealed class DerivedOeeEvidenceCut
{
    public DerivedOeeEvidenceCut(QualityAssessmentScope scope, QualityEvidenceReference decision, DerivedOeeCutState state,
        OperationalMetricProjectionProcessorId projectionProcessor, MetricAggregationCheckpoint metricRevision,
        string qualityIdentity, string qualityContentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(projectionProcessor); ArgumentNullException.ThrowIfNull(metricRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualityIdentity); ArgumentException.ThrowIfNullOrWhiteSpace(qualityContentFingerprint);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (decision.Kind != QualityEvidenceKind.SourceDependency || metricRevision.StreamId.MachineId != scope.MachineId)
            throw new ArgumentException("An authority-owned coherence decision and matching machine revision are required.", nameof(decision));
        Scope = scope; Decision = decision; State = state; ProjectionProcessor = projectionProcessor;
        MetricRevision = metricRevision; QualityIdentity = qualityIdentity; QualityContentFingerprint = qualityContentFingerprint;
    }
    public QualityAssessmentScope Scope { get; }
    public QualityEvidenceReference Decision { get; }
    public DerivedOeeCutState State { get; }
    public OperationalMetricProjectionProcessorId ProjectionProcessor { get; }
    public MetricAggregationCheckpoint MetricRevision { get; }
    public string QualityIdentity { get; }
    public string QualityContentFingerprint { get; }
}

/// <summary>Immutable canonical A/P snapshot with explicit interval binding from its reporting authority.</summary>
public sealed class DerivedOeeMetricDependency
{
    public DerivedOeeMetricDependency(QualityAssessmentScope scope, OperationalMetricProjection projection)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(projection);
        if (projection.Key.MachineId != scope.MachineId || projection.Key.PeriodId != scope.PeriodId || projection.Key.ContextKey != scope.Context)
            throw new ArgumentException("Canonical dependency must match the exact reporting scope.", nameof(projection));
        var availability = projection.Key.DefinitionId == BuiltInOperationalMetricDefinitions.AvailabilityId;
        if ((!availability && projection.Key.DefinitionId != BuiltInOperationalMetricDefinitions.PerformanceId) ||
            projection.Unit != OperationalMetricUnits.Ratio || !Enum.IsDefined(projection.Status) ||
            projection.ReasonCode is { } reason && !Enum.IsDefined(reason) || projection.DependencyEvidence.Count != 0)
            throw new ArgumentException("Only canonical A/P v1 ratio snapshots are supported.", nameof(projection));
        var definition = BuiltInOperationalMetricDefinitions.All.Single(item => item.Id == projection.Key.DefinitionId);
        foreach (var item in projection.OperandEvidence)
        {
            var operand = definition.Operands.SingleOrDefault(operand => operand.OperandName == item.OperandName);
            if (operand?.Source is not OperationalMetricOperandSource.Component component ||
                !OperationalMetricComponentKeyMapping.Matches(component.ComponentKey, item.SourceIdentity.ComponentKey) ||
                item.Dimension != operand.RequiredDimension || item.Unit != operand.RequiredUnit || item.Value < 0)
                throw new ArgumentException("Invalid canonical component evidence.", nameof(projection));
        }
        if (projection.Status == OperationalMetricEvaluationStatus.Calculated)
        {
            var formula = (OperationalMetricFormula.Ratio)definition.Formula;
            var numerator = projection.OperandEvidence.SingleOrDefault(item => item.OperandName == formula.NumeratorOperand);
            var denominator = projection.OperandEvidence.SingleOrDefault(item => item.OperandName == formula.DenominatorOperand);
            if (numerator is null || denominator is null || denominator.Value <= 0 || projection.Value < 0 ||
                availability && projection.Value > 1 || decimal.Round(numerator.Value / denominator.Value,
                    definition.PrecisionPolicy.DurableDecimalScale, definition.PrecisionPolicy.RoundingMode) != projection.Value)
                throw new ArgumentException("Calculated dependency must match its canonical operand evidence and precision.", nameof(projection));
        }
        Scope = scope;
        Projection = new OperationalMetricProjection(projection.ProcessorId, projection.Key, projection.Status, projection.Value,
            projection.Unit, projection.ReasonCode, projection.ReasonOperandName, projection.SourceRevision,
            projection.OperandEvidence.OrderBy(static item => item.OperandName, StringComparer.Ordinal));
    }
    public QualityAssessmentScope Scope { get; }
    public OperationalMetricProjection Projection { get; }
}

public sealed class DerivedOeeTarget
{
    public DerivedOeeTarget(DerivedOeeEvidenceCut cut, DerivedOeeMetricDependency availability,
        DerivedOeeMetricDependency performance, QualityAssessment quality, string ruleVersion)
    {
        ArgumentNullException.ThrowIfNull(cut); ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(performance); ArgumentNullException.ThrowIfNull(quality);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);
        if (availability.Scope != cut.Scope || performance.Scope != cut.Scope || quality.Target.Scope != cut.Scope ||
            availability.Projection.Key.DefinitionId != BuiltInOperationalMetricDefinitions.AvailabilityId ||
            performance.Projection.Key.DefinitionId != BuiltInOperationalMetricDefinitions.PerformanceId ||
            availability.Projection.ProcessorId != cut.ProjectionProcessor || performance.Projection.ProcessorId != cut.ProjectionProcessor ||
            availability.Projection.SourceRevision != cut.MetricRevision || performance.Projection.SourceRevision != cut.MetricRevision ||
            quality.Identity != cut.QualityIdentity || quality.ContentFingerprint != cut.QualityContentFingerprint)
            throw new ArgumentException("Dependencies must match the exact scope, metric revision and explicitly bound Quality assessment.", nameof(cut));
        var actualA = availability.Projection.OperandEvidence.SingleOrDefault(static item => item.OperandName == "ActualProductionTime");
        var actualP = performance.Projection.OperandEvidence.SingleOrDefault(static item => item.OperandName == "ActualProductionTime");
        if (actualA is not null && actualP is not null && actualA != actualP)
            throw new ArgumentException("Shared actual-production-time evidence differs within one cut.", nameof(performance));
        Cut = cut; Availability = availability; Performance = performance; Quality = quality; RuleVersion = ruleVersion;
    }
    public DerivedOeeEvidenceCut Cut { get; }
    public DerivedOeeMetricDependency Availability { get; }
    public DerivedOeeMetricDependency Performance { get; }
    public QualityAssessment Quality { get; }
    public string RuleVersion { get; }
}

public sealed record DerivedOeeDependencyFailure(string Dependency, OperationalMetricEvaluationStatus? MetricStatus,
    OperationalMetricEvaluationReasonCode? MetricReason, string? OperandName, QualityAssessmentReason? QualityReason);

public sealed class DerivedOeeAssessment
{
    internal DerivedOeeAssessment(DerivedOeeTarget target, DerivedOeeReason reason, decimal? value,
        IEnumerable<DerivedOeeDependencyFailure> failures)
    {
        Target = target; Reason = reason; Value = value; Failures = Array.AsReadOnly(failures.ToArray());
        Identity = DerivedOeeIdentity.Create(target); ContentFingerprint = DerivedOeeIdentity.ContentFingerprint(target);
        if (target.Quality.Basis == QualityAssessmentBasis.PolicyAssumed)
            AssumedPolicy = target.Quality.Target.Policies.Single(policy => policy.Identity == target.Quality.AppliedPolicy);
    }
    public DerivedOeeTarget Target { get; }
    public string Identity { get; }
    public string ContentFingerprint { get; }
    public DerivedOeeReason Reason { get; }
    public decimal? Value { get; }
    public IReadOnlyList<DerivedOeeDependencyFailure> Failures { get; }
    public ApprovedAllGoodQualityPolicy? AssumedPolicy { get; }
    public bool IsProvisional => Value.HasValue && AssumedPolicy is not null;
}

public static class DerivedOeeAssessor
{
    public const string RuleVersion = "1.0";
    public static DerivedOeeAssessment Assess(DerivedOeeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.RuleVersion != RuleVersion) throw new NotSupportedException("Unsupported OEE derivation rule.");
        var failures = new List<DerivedOeeDependencyFailure>();
        foreach (var dependency in new[] { target.Availability, target.Performance })
        {
            var projection = dependency.Projection;
            if (projection.Status != OperationalMetricEvaluationStatus.Calculated)
                failures.Add(new(projection.Key.DefinitionId.MetricKey, projection.Status, projection.ReasonCode, projection.ReasonOperandName, null));
        }
        if (!target.Quality.IsAvailable) failures.Add(new("quality", null, null, null, target.Quality.Reason));
        if (target.Cut.State != DerivedOeeCutState.Established)
            return new(target, target.Cut.State == DerivedOeeCutState.Unproven ? DerivedOeeReason.CoherenceUnproven : DerivedOeeReason.CoherenceConflicting, null, failures);
        if (failures.Count != 0) return new(target, DerivedOeeReason.DependenciesUnavailable, null, failures);
        var value = target.Availability.Projection.Value!.Value * target.Performance.Projection.Value!.Value * target.Quality.Value!.Value;
        return value > 1m ? new(target, DerivedOeeReason.ProductOutOfRange, null, failures) : new(target, DerivedOeeReason.Calculated, value, failures);
    }
}

public static class DerivedOeeSelector
{
    public static DerivedOeeAssessment? Select(DerivedOeeTarget? target, IEnumerable<DerivedOeeAssessment> assessments)
    {
        ArgumentNullException.ThrowIfNull(assessments);
        var snapshot = assessments.ToArray();
        if (snapshot.Any(static item => item is null)) throw new ArgumentException("Null OEE assessment.", nameof(assessments));
        foreach (var group in snapshot.GroupBy(static item => item.Identity, StringComparer.Ordinal))
            if (group.Select(static item => item.ContentFingerprint).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new InvalidDataException("Different OEE content under one identity.");
        if (target is null) return null;
        var identity = DerivedOeeIdentity.Create(target);
        var found = snapshot.FirstOrDefault(item => item.Identity == identity);
        if (found is not null && found.ContentFingerprint != DerivedOeeIdentity.ContentFingerprint(target))
            throw new InvalidDataException("OEE target content conflicts with its immutable identity.");
        return found;
    }
}

public sealed record DerivedOeeReadResult(QualityStorageReadOutcome Outcome, DerivedOeeAssessment? Assessment);
public interface IDerivedOeeAssessmentStore
{
    ValueTask<QualityPublicationOutcome> PublishAsync(DerivedOeeAssessment assessment, CancellationToken cancellationToken);
    ValueTask<DerivedOeeReadResult> ReadByIdentityAsync(string identity, CancellationToken cancellationToken);
    ValueTask<DerivedOeeReadResult> ReadExactAsync(DerivedOeeTarget? target, CancellationToken cancellationToken);
}
