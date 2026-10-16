using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public enum RunningEvidenceState { Missing, Partial, Established, Incomparable, Corrupt }
public enum RunningEvidenceReason { RevisionAbsent, OperandAbsent, CoverageAuthorityNotEstablished, ScopeMismatch, PersistedEvidenceInvalid }

public sealed record RunningProvenanceRequest(MetricAggregationCheckpoint Revision,
    ShiftOccurrenceId Shift, ProductionDayId ProductionDay, OperationalMetricCoverageInterval Extent)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Revision); ArgumentNullException.ThrowIfNull(Shift);
        ArgumentNullException.ThrowIfNull(ProductionDay); ArgumentNullException.ThrowIfNull(Extent);
        if (Shift.SiteId != ProductionDay.SiteId || Extent.StartsAtUtc < Shift.StartsAtUtc || Extent.EndsAtUtc > Shift.EndsAtUtc)
            throw new ArgumentException("Running extent must belong to the exact requested shift and production day.");
    }
}

public sealed record RunningEvidenceSlice(string EligibilityId, string ActivityId,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, MachineState State, bool HasStateContribution,
    string ObservationProcessorId, string ObservationStreamKey, ulong ObservationPosition,
    ulong InstanceId, ulong Sequence, string EligibilityPayloadHash, string ActivityPayloadHash, string ActivityHistoryFingerprint,
    ImmutableArray<MetricInputPosition> ContributionPositions);

/// <summary>Observed duration is not continuity or full-period authority. This slice never emits Established.</summary>
public sealed record RunningProvenanceResult(RunningProvenanceRequest Request, RunningEvidenceState State,
    RunningEvidenceReason Reason, TimeSpan? ObservedRunningDuration, ImmutableArray<RunningEvidenceSlice> Slices,
    ImmutableArray<OperationalMetricCoverageInterval> UnobservedIntervals,
    ImmutableArray<OperationalMetricCoverageInterval> UnavailableIntervals, bool HasInstanceBoundary, string EvidenceFingerprint);

public interface IRunningProvenanceReader
{
    ValueTask<RunningProvenanceResult> ReadExactAsync(RunningProvenanceRequest request, CancellationToken cancellationToken);
}

/// <summary>Deterministic structural reconciliation for already resolved durable source slices.
/// Neither adjacency nor ledger presence is promoted to a coverage-authority claim.</summary>
public static class RunningProvenanceReconciler
{
    public static RunningProvenanceResult Reconcile(RunningProvenanceRequest request, IEnumerable<RunningEvidenceSlice> slices)
    {
        request.Validate(); ArgumentNullException.ThrowIfNull(slices);
        var ordered = slices.OrderBy(x => x.StartsAtUtc).ThenBy(x => x.EndsAtUtc).ThenBy(x => x.EligibilityId, StringComparer.Ordinal).ToImmutableArray();
        var cursor = request.Extent.StartsAtUtc;
        var gaps = ImmutableArray.CreateBuilder<OperationalMetricCoverageInterval>();
        var unavailable = ImmutableArray.CreateBuilder<OperationalMetricCoverageInterval>();
        var known = false; var running = TimeSpan.Zero; ulong? instance = null; var boundary = false;
        var eligibilityIds = new HashSet<string>(StringComparer.Ordinal);
        var positions = new HashSet<ulong>();
        string? processor = null; string? streamKey = null;
        foreach (var slice in ordered)
        {
            if (slice.StartsAtUtc < cursor || slice.EndsAtUtc <= slice.StartsAtUtc || slice.EndsAtUtc > request.Extent.EndsAtUtc ||
                !Enum.IsDefined(slice.State) || string.IsNullOrWhiteSpace(slice.EligibilityId) || string.IsNullOrWhiteSpace(slice.ActivityId) ||
                string.IsNullOrWhiteSpace(slice.ObservationProcessorId) || string.IsNullOrWhiteSpace(slice.ObservationStreamKey) ||
                slice.ObservationPosition == 0 || slice.ContributionPositions.IsDefaultOrEmpty ||
                slice.ContributionPositions.Any(x => x.Value > request.Revision.Position.Value || !positions.Add(x.Value)) ||
                !eligibilityIds.Add(slice.EligibilityId) ||
                string.IsNullOrWhiteSpace(slice.EligibilityPayloadHash) || string.IsNullOrWhiteSpace(slice.ActivityPayloadHash) ||
                string.IsNullOrWhiteSpace(slice.ActivityHistoryFingerprint))
                return Failure(request, RunningEvidenceState.Corrupt, RunningEvidenceReason.PersistedEvidenceInvalid);
            if (processor is not null && (processor != slice.ObservationProcessorId || streamKey != slice.ObservationStreamKey))
                return Failure(request, RunningEvidenceState.Incomparable, RunningEvidenceReason.ScopeMismatch);
            processor = slice.ObservationProcessorId; streamKey = slice.ObservationStreamKey;
            if (slice.StartsAtUtc > cursor) gaps.Add(new(cursor, slice.StartsAtUtc));
            cursor = slice.EndsAtUtc;
            if (!slice.HasStateContribution || slice.State is MachineState.Unknown or MachineState.Offline) unavailable.Add(new(slice.StartsAtUtc, slice.EndsAtUtc));
            else { known = true; if (slice.State == MachineState.Running) running += slice.EndsAtUtc - slice.StartsAtUtc; }
            if (instance is not null && instance != slice.InstanceId) boundary = true;
            instance = slice.InstanceId;
        }
        if (cursor < request.Extent.EndsAtUtc) gaps.Add(new(cursor, request.Extent.EndsAtUtc));
        var state = known ? RunningEvidenceState.Partial : RunningEvidenceState.Missing;
        var reason = known ? RunningEvidenceReason.CoverageAuthorityNotEstablished : RunningEvidenceReason.OperandAbsent;
        return new(request, state, reason, known ? running : null, ordered, gaps.ToImmutable(), unavailable.ToImmutable(), boundary,
            Fingerprint(request, state, reason, ordered));
    }

    public static RunningProvenanceResult Failure(RunningProvenanceRequest request, RunningEvidenceState state, RunningEvidenceReason reason) =>
        new(request, state, reason, null, [], [], [], false, Fingerprint(request, state, reason, []));

    private static string Fingerprint(RunningProvenanceRequest q, RunningEvidenceState state, RunningEvidenceReason reason, ImmutableArray<RunningEvidenceSlice> slices)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
        {
            w.Write(1); w.Write(q.Revision.ProcessorId.Value); w.Write(q.Revision.StreamId.MachineId.Value.ToByteArray());
            w.Write(q.Revision.StreamId.StreamKey); w.Write(q.Revision.Position.Value);
            w.Write(q.Shift.SiteId.Value); w.Write(q.Shift.ShiftScheduleAssignmentId.Value); w.Write(q.Shift.ShiftId.Value);
            w.Write(q.Shift.StartsAtUtc.UtcTicks); w.Write(q.Shift.EndsAtUtc.UtcTicks); w.Write(q.ProductionDay.BusinessDate.DayNumber);
            w.Write(q.Extent.StartsAtUtc.UtcTicks); w.Write(q.Extent.EndsAtUtc.UtcTicks); w.Write((int)state); w.Write((int)reason); w.Write(slices.Length);
            foreach (var s in slices)
            {
                w.Write(s.EligibilityId); w.Write(s.ActivityId); w.Write(s.StartsAtUtc.UtcTicks); w.Write(s.EndsAtUtc.UtcTicks); w.Write((int)s.State); w.Write(s.HasStateContribution);
                w.Write(s.ObservationProcessorId); w.Write(s.ObservationStreamKey); w.Write(s.ObservationPosition); w.Write(s.InstanceId); w.Write(s.Sequence);
                w.Write(s.EligibilityPayloadHash); w.Write(s.ActivityPayloadHash); w.Write(s.ActivityHistoryFingerprint);
                var positions = s.ContributionPositions.OrderBy(x => x.Value).ToArray(); w.Write(positions.Length);
                foreach (var position in positions) w.Write(position.Value);
            }
        }
        return "running-provenance:v1:" + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
