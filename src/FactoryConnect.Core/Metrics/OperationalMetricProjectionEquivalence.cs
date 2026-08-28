using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public static class OperationalMetricProjectionEquivalence
{
    public static bool AreEquivalent(
        OperationalMetricProjection left,
        OperationalMetricProjection right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.ProcessorId == right.ProcessorId &&
               left.Key == right.Key &&
               left.Status == right.Status &&
               left.Value == right.Value &&
               string.Equals(left.Unit, right.Unit, StringComparison.Ordinal) &&
               left.ReasonCode == right.ReasonCode &&
               string.Equals(left.ReasonOperandName, right.ReasonOperandName, StringComparison.Ordinal) &&
               left.SourceRevision == right.SourceRevision &&
               ComponentEvidenceEquivalent(left.OperandEvidence, right.OperandEvidence) &&
               DependencyEvidenceEquivalent(left.DependencyEvidence, right.DependencyEvidence);
    }

    private static bool ComponentEvidenceEquivalent(
        IReadOnlyList<OperationalMetricComponentProjectionEvidence> left,
        IReadOnlyList<OperationalMetricComponentProjectionEvidence> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftEvidence = left[index];
            var rightEvidence = right[index];
            if (!string.Equals(leftEvidence.OperandName, rightEvidence.OperandName, StringComparison.Ordinal) ||
                leftEvidence.SourceIdentity != rightEvidence.SourceIdentity ||
                leftEvidence.SourceRevision != rightEvidence.SourceRevision ||
                leftEvidence.Dimension != rightEvidence.Dimension ||
                leftEvidence.Value != rightEvidence.Value ||
                !string.Equals(leftEvidence.Unit, rightEvidence.Unit, StringComparison.Ordinal) ||
                leftEvidence.InputCount != rightEvidence.InputCount ||
                leftEvidence.FirstInputTimestamp != rightEvidence.FirstInputTimestamp ||
                leftEvidence.LastInputTimestamp != rightEvidence.LastInputTimestamp)
            {
                return false;
            }
        }

        return true;
    }

    private static bool DependencyEvidenceEquivalent(
        IReadOnlyList<OperationalMetricDependencyProjectionEvidence> left,
        IReadOnlyList<OperationalMetricDependencyProjectionEvidence> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftEvidence = left[index];
            var rightEvidence = right[index];
            if (!string.Equals(leftEvidence.OperandName, rightEvidence.OperandName, StringComparison.Ordinal) ||
                leftEvidence.DefinitionId != rightEvidence.DefinitionId ||
                !AreEquivalent(leftEvidence.Projection, rightEvidence.Projection))
            {
                return false;
            }
        }

        return true;
    }
}
