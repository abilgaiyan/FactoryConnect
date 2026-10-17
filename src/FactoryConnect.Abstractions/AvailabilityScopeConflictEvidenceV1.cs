namespace FactoryConnect.Abstractions;

/// <summary>Opt-in evaluation policy. Not a production registration or a persistence codec.</summary>
public static class AvailabilityContainmentV1
{
    public const string PolicyId = "availability-scope-containment/1";
    // The denominator is the sum of duration.planned-production facts derived
    // from eligibility=true with a planned assignment. This says nothing about
    // whether the effective schedule is approved or configured correctly.
    public const string PlannedProductionOperandContractId = "duration.planned-production/eligibility-v1";

    public static OperationalMetricProjectionProcessorId ProjectionProcessorId(MachineId machineId) =>
        new($"operational-metrics:{machineId.Value}:builtins-v1-availability-containment-v1");
}

/// <summary>
/// Positive contribution-membership proof, constructed only by the Core builder
/// from a committed historical cut. Get-only properties prevent record mutation.
/// </summary>
public sealed record AvailabilityScopeConflictEvidenceV1
{
    internal AvailabilityScopeConflictEvidenceV1(
        MetricAggregationCheckpoint revision,
        OperationalMetricComponent numerator,
        OperationalMetricComponent denominator,
        PositionedMetricInputFact contribution)
    {
        SourceRevision = revision;
        Numerator = numerator;
        Denominator = denominator;
        Contribution = contribution;
    }

    public string RuleId { get; } = AvailabilityContainmentV1.PolicyId;
    public string PlannedProductionOperandContractId { get; } = AvailabilityContainmentV1.PlannedProductionOperandContractId;
    public MetricAggregationCheckpoint SourceRevision { get; }
    public OperationalMetricComponent Numerator { get; }
    public OperationalMetricComponent Denominator { get; }
    public PositionedMetricInputFact Contribution { get; }

    internal void ValidateBinding(
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision,
        IEnumerable<OperationalMetricComponent> components)
    {
        var supplied = components.ToArray();
        if (SourceRevision != revision || Numerator.SourceIdentity.PeriodId != key.PeriodId ||
            Numerator.SourceIdentity.MachineId != key.MachineId ||
            !supplied.Any(component => Matches(component, Numerator)) ||
            !supplied.Any(component => Matches(component, Denominator)))
        {
            throw new ArgumentException("Conflict witness does not bind to the exact snapshot and operand aggregates.", nameof(components));
        }
    }

    private static bool Matches(OperationalMetricComponent left, OperationalMetricComponent right) =>
        left.SourceIdentity == right.SourceIdentity && left.Dimension == right.Dimension && left.Aggregate == right.Aggregate;
}
