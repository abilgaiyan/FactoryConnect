using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

// Claims retained in the canonical result, not a certificate of completed authority.
internal sealed class ContainmentHistoricalAuthority
{
    public ContainmentHistoricalAuthority(long completedReferenceRevision, IEnumerable<string> sourceQuantityIds, IEnumerable<long> standardRevisions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedReferenceRevision);
        ArgumentNullException.ThrowIfNull(sourceQuantityIds);
        ArgumentNullException.ThrowIfNull(standardRevisions);
        CompletedReferenceRevision = completedReferenceRevision;
        SourceQuantityIds = Array.AsReadOnly(sourceQuantityIds.Take(65_536).ToArray());
        StandardRevisions = Array.AsReadOnly(standardRevisions.Take(65_536).ToArray());
        if (SourceQuantityIds.Count > 65_535 || StandardRevisions.Count > 65_535 ||
            SourceQuantityIds.Any(string.IsNullOrEmpty) || StandardRevisions.Any(static revision => revision < 0) ||
            !SourceQuantityIds.SequenceEqual(SourceQuantityIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) ||
            !StandardRevisions.SequenceEqual(StandardRevisions.Distinct().Order()))
            throw new InvalidDataException("Noncanonical historical authority set.");
    }
    public long CompletedReferenceRevision { get; }
    public IReadOnlyList<string> SourceQuantityIds { get; }
    public IReadOnlyList<long> StandardRevisions { get; }
}

internal sealed record ContainmentDependencyDocument(string OperandName, OperationalMetricDefinitionId DefinitionId, ContainmentResultDocument Result);

/// <summary>
/// Unverified wire representation. Deliberately cannot be passed to the projection
/// commit coordinator or converted to a Stage-A witness by the decoder.
/// </summary>
internal sealed class ContainmentResultDocument
{
    public ContainmentResultDocument(OperationalMetricProjectionProcessorId processorId, OperationalMetricEvaluationKey key,
        OperationalMetricEvaluationStatus status, decimal? value, string unit, OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName, MetricAggregationCheckpoint sourceRevision, ContainmentHistoricalAuthority? authority,
        ContainmentWitnessDocument? witness, IEnumerable<OperationalMetricComponentProjectionEvidence> components,
        IEnumerable<ContainmentDependencyDocument> dependencies)
    {
        ProcessorId = processorId; Key = key; Status = status; Value = value; Unit = unit;
        ReasonCode = reasonCode; ReasonOperandName = reasonOperandName; SourceRevision = sourceRevision;
        Authority = authority; Witness = witness;
        Components = Array.AsReadOnly(components.Take(65_536).ToArray()); Dependencies = Array.AsReadOnly(dependencies.Take(65_536).ToArray());
        if (Components.Count > 65_535 || Dependencies.Count > 65_535)
            throw new InvalidDataException("Containment per-kind evidence count exceeded.");
    }
    public OperationalMetricProjectionProcessorId ProcessorId { get; }
    public OperationalMetricEvaluationKey Key { get; }
    public OperationalMetricEvaluationStatus Status { get; }
    public decimal? Value { get; }
    public string Unit { get; }
    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }
    public string? ReasonOperandName { get; }
    public MetricAggregationCheckpoint SourceRevision { get; }
    public ContainmentHistoricalAuthority? Authority { get; }
    public ContainmentWitnessDocument? Witness { get; }
    public IReadOnlyList<OperationalMetricComponentProjectionEvidence> Components { get; }
    public IReadOnlyList<ContainmentDependencyDocument> Dependencies { get; }

    public static ContainmentResultDocument FromProjection(OperationalMetricProjection projection,
        Func<OperationalMetricProjection, ContainmentHistoricalAuthority?> authoritySelector) => FromProjection(projection, authoritySelector, 1);

    private static ContainmentResultDocument FromProjection(OperationalMetricProjection projection,
        Func<OperationalMetricProjection, ContainmentHistoricalAuthority?> authoritySelector, int depth)
    {
        ArgumentNullException.ThrowIfNull(projection); ArgumentNullException.ThrowIfNull(authoritySelector);
        if (depth > 64) throw new InvalidDataException("Containment dependency depth exceeded.");
        if (projection.EvaluationPolicyId != AvailabilityContainmentV1.PolicyId)
            throw new InvalidDataException("V2 requires an explicit containment policy.");
        return new(projection.ProcessorId, projection.Key, projection.Status, projection.Value, projection.Unit,
            projection.ReasonCode, projection.ReasonOperandName, projection.SourceRevision, authoritySelector(projection),
            projection.AvailabilityScopeConflictEvidence is { } witness ? ContainmentWitnessDocument.FromEvidence(witness) : null,
            projection.OperandEvidence, projection.DependencyEvidence.Select(d => new ContainmentDependencyDocument(
                d.OperandName, d.DefinitionId, FromProjection(d.Projection, authoritySelector, depth + 1))));
    }
}
