using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class RunningProvenanceTests
{
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
    private static RunningProvenanceRequest Request() => new(new(new MetricAggregationProcessorId("agg"),
        MetricInputStreamId.ForMachine(Machine), new MetricInputPosition(10)),
        new(new SiteId("GG"), new ShiftScheduleAssignmentId("schedule"), new ShiftId("SHIFT-1"), Start, Start.AddHours(8)),
        new(new SiteId("GG"), new DateOnly(2026, 10, 16)), new(Start, Start.AddHours(8)));
    private static RunningEvidenceSlice Slice(string id, int from, int to, MachineState state, ulong instance = 1) =>
        new(id, "activity-" + id, Start.AddHours(from), Start.AddHours(to), state, true,
            "state", "mtconnect", 1, instance, 1, "eligibility-hash", "activity-hash", "history-hash", [new MetricInputPosition((ulong)from + 1)]);

    [Fact]
    public void MissingEvidenceIsNullRatherThanZero()
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(), []);
        Assert.Equal(RunningEvidenceState.Missing, result.State); Assert.Null(result.ObservedRunningDuration);
        Assert.Single(result.UnobservedIntervals);
    }
    [Fact]
    public void ObservedRunningRetainsGapsAndCannotClaimContinuity()
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(), [Slice("one", 1, 2, MachineState.Running)]);
        Assert.Equal(RunningEvidenceState.Partial, result.State); Assert.Equal(TimeSpan.FromHours(1), result.ObservedRunningDuration);
        Assert.Equal(2, result.UnobservedIntervals.Length);
        Assert.Equal(RunningEvidenceReason.CoverageAuthorityNotEstablished, result.Reason);
    }
    [Theory]
    [InlineData(MachineState.Running)]
    [InlineData(MachineState.Stopped)]
    public void CompleteGeometryDoesNotProveCoverageAndKnownNonRunningIsObservedZero(MachineState state)
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(), [Slice("all", 0, 8, state)]);
        Assert.Equal(state == MachineState.Running ? TimeSpan.FromHours(8) : TimeSpan.Zero, result.ObservedRunningDuration);
        Assert.Empty(result.UnobservedIntervals); Assert.Equal(RunningEvidenceState.Partial, result.State);
    }
    [Theory]
    [InlineData(MachineState.Unknown)]
    [InlineData(MachineState.Offline)]
    public void UnavailableStateDoesNotManufactureZero(MachineState state)
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(), [Slice("all", 0, 8, state)]);
        Assert.Null(result.ObservedRunningDuration); Assert.Equal(RunningEvidenceState.Missing, result.State);
        Assert.Single(result.UnavailableIntervals);
    }
    [Fact]
    public void StateFactBeyondCutIsNotUsedEvenWhenItsSourcePayloadExists()
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(), [Slice("all", 0, 8, MachineState.Running) with { HasStateContribution = false }]);
        Assert.Null(result.ObservedRunningDuration); Assert.Single(result.UnavailableIntervals);
    }
    [Fact]
    public void InstanceBoundaryIsRetainedAndNeverPromotedToEstablished()
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(),
            [Slice("first", 0, 4, MachineState.Running), Slice("second", 4, 8, MachineState.Stopped, 2)]);
        Assert.True(result.HasInstanceBoundary); Assert.Equal(TimeSpan.FromHours(4), result.ObservedRunningDuration);
        Assert.Equal(RunningEvidenceState.Partial, result.State);
    }
    [Fact]
    public void ConflictingOverlappingSourcesFailClosed()
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(),
            [Slice("first", 0, 5, MachineState.Running), Slice("second", 4, 8, MachineState.Stopped)]);
        Assert.Equal(RunningEvidenceState.Corrupt, result.State); Assert.Null(result.ObservedRunningDuration);
    }
    [Fact]
    public void DifferentObservationStreamsCannotBeCombinedAsOneBinding()
    {
        var result = RunningProvenanceReconciler.Reconcile(Request(),
            [Slice("first", 0, 4, MachineState.Running), Slice("second", 4, 8, MachineState.Stopped) with { ObservationStreamKey = "other-stream" }]);
        Assert.Equal(RunningEvidenceState.Incomparable, result.State); Assert.Null(result.ObservedRunningDuration);
    }
    [Fact]
    public void ActivityPublicationRejectsMultipleOutputsAtOneObservationPosition()
    {
        var processor = new ObservationProcessorId("state"); var stream = new ObservationStreamId(Machine, "mtconnect");
        var position = new ObservationPosition(1);
        var projection = new MachineStateActivityProjection(processor, stream, position, [], MachineState.Stopped, MachineState.Stopped, Start.AddHours(8));
        var first = new DurableMachineActivityPeriod(processor, position, stream, 1, 1,
            new(Machine, MachineState.Running, Start, Start.AddHours(4)));
        var second = new DurableMachineActivityPeriod(processor, position, stream, 1, 1,
            new(Machine, MachineState.Stopped, Start.AddHours(4), Start.AddHours(8)));
        var identity = new EvaluationAuthorityReplayIdentity(processor, stream, position, MachineState.Stopped, 1,
            new CurrentStatePolicyReference("continuity/default", "1.0"));
        var error = Assert.Throws<ArgumentException>(() => new MachineStateActivityAuthorityPublication(null, null, projection, [], [first, second], identity));
        Assert.Contains("strictly increasing", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void OrderingReplayAndChangedRevisionHaveDeterministicBindings()
    {
        var q = Request(); var a = Slice("first", 0, 4, MachineState.Running); var b = Slice("second", 4, 8, MachineState.Stopped);
        var first = RunningProvenanceReconciler.Reconcile(q, [a, b]);
        Assert.Equal(first.EvidenceFingerprint, RunningProvenanceReconciler.Reconcile(q, [b, a]).EvidenceFingerprint);
        var changed = q with { Revision = new(q.Revision.ProcessorId, q.Revision.StreamId, new MetricInputPosition(11)) };
        Assert.NotEqual(first.EvidenceFingerprint, RunningProvenanceReconciler.Reconcile(changed, [a, b]).EvidenceFingerprint);
    }
}
