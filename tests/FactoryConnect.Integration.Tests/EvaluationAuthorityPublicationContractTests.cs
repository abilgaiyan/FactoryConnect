using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class EvaluationAuthorityPublicationContractTests
{
    [Fact]
    public void ExpectedRevisionDoesNotParticipateInReplayIdentity()
    {
        var identity = Identity();
        var policy = Policy("continuity/default", "1.0");

        var first = Publication(
            null,
            identity,
            evaluatedThrough: 10,
            machineState: MachineState.Running,
            instanceId: 42,
            policy);
        var retry = Publication(
            new StateProjectionAuthorityRevision(999),
            identity,
            evaluatedThrough: 10,
            machineState: MachineState.Running,
            instanceId: 42,
            policy);

        Assert.Equal(first.ReplayIdentity, retry.ReplayIdentity);
        Assert.NotEqual(first.ExpectedRevision, retry.ExpectedRevision);
    }

    [Fact]
    public void ReplayIdentityIncludesEverySemanticPublicationField()
    {
        var identity = Identity();
        var policy = Policy("continuity/default", "1.0");
        var baseline = Publication(
            null,
            identity,
            evaluatedThrough: 10,
            machineState: MachineState.Running,
            instanceId: 42,
            policy).ReplayIdentity;

        Assert.NotEqual(
            baseline,
            Publication(
                null,
                (new ObservationProcessorId("state-alt"), identity.StreamId),
                10,
                MachineState.Running,
                42,
                policy).ReplayIdentity);
        Assert.NotEqual(
            baseline,
            Publication(
                null,
                (identity.ProcessorId,
                    new ObservationStreamId(identity.StreamId.MachineId, "MTConnect:CNC-02")),
                10,
                MachineState.Running,
                42,
                policy).ReplayIdentity);
        Assert.NotEqual(
            baseline,
            Publication(
                null,
                identity,
                11,
                MachineState.Running,
                42,
                policy).ReplayIdentity);
        Assert.NotEqual(
            baseline,
            Publication(
                null,
                identity,
                10,
                MachineState.Faulted,
                42,
                policy).ReplayIdentity);
        Assert.NotEqual(
            baseline,
            Publication(
                null,
                identity,
                10,
                MachineState.Running,
                43,
                policy).ReplayIdentity);
        Assert.NotEqual(
            baseline,
            Publication(
                null,
                identity,
                10,
                MachineState.Running,
                42,
                Policy("continuity/reset", "1.0")).ReplayIdentity);
        Assert.NotEqual(
            baseline,
            Publication(
                null,
                identity,
                10,
                MachineState.Running,
                42,
                Policy("continuity/default", "2.0")).ReplayIdentity);
    }

    [Fact]
    public void PublicationProjectsSemanticFieldsFromReplayIdentity()
    {
        var identity = Identity();
        var expected = new StateProjectionAuthorityRevision(7);
        var policy = Policy("continuity/preserve", "3.0");
        var publication = Publication(
            expected,
            identity,
            evaluatedThrough: 25,
            machineState: MachineState.Idle,
            instanceId: 77,
            policy);

        Assert.Equal(expected, publication.ExpectedRevision);
        Assert.Equal(identity.ProcessorId, publication.StateProcessorId);
        Assert.Equal(identity.StreamId, publication.ObservationStreamId);
        Assert.Equal(new ObservationPosition(25), publication.EvaluatedThrough);
        Assert.Equal(MachineState.Idle, publication.MachineState);
        Assert.Equal(77UL, publication.LastConsumedInstanceId);
        Assert.Equal(policy, publication.AppliedContinuityPolicy);
    }

    [Fact]
    public void ReplayIdentityRejectsUndefinedMachineState()
    {
        var identity = Identity();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EvaluationAuthorityReplayIdentity(
                identity.ProcessorId,
                identity.StreamId,
                new ObservationPosition(1),
                (MachineState)int.MaxValue,
                1,
                Policy("continuity/default", "1.0")));
    }

    [Fact]
    public void AcceptedResultCarriesProviderAllocatedAuthorityAndDisposition()
    {
        var identity = Identity();
        var policy = Policy("continuity/default", "1.0");
        var authority = new EvaluationAuthority(
            identity.ProcessorId,
            identity.StreamId,
            new ObservationPosition(10),
            MachineState.Running,
            42,
            policy,
            new StateProjectionAuthorityRevision(5));

        var result = new EvaluationAuthorityPublicationAccepted(
            authority,
            EvaluationAuthorityPublicationDisposition.ExactReplay);

        Assert.Same(authority, result.Authority);
        Assert.Equal(
            EvaluationAuthorityPublicationDisposition.ExactReplay,
            result.Disposition);
    }

    [Fact]
    public void PublicationResultTypesMakeConflictAndExhaustionExplicit()
    {
        var identity = Identity();
        var authority = new EvaluationAuthority(
            identity.ProcessorId,
            identity.StreamId,
            new ObservationPosition(10),
            MachineState.Running,
            42,
            Policy("continuity/default", "1.0"),
            new StateProjectionAuthorityRevision(ulong.MaxValue));

        EvaluationAuthorityPublicationResult conflict =
            new EvaluationAuthorityPublicationConflict(authority);
        EvaluationAuthorityPublicationResult exhausted =
            new EvaluationAuthorityRevisionExhausted(authority);

        Assert.IsType<EvaluationAuthorityPublicationConflict>(conflict);
        Assert.IsType<EvaluationAuthorityRevisionExhausted>(exhausted);
        Assert.Same(
            authority,
            ((EvaluationAuthorityPublicationConflict)conflict).CurrentAuthority);
        Assert.Same(
            authority,
            ((EvaluationAuthorityRevisionExhausted)exhausted).CurrentAuthority);
    }

    [Fact]
    public void AcceptedResultRejectsUndefinedDisposition()
    {
        var identity = Identity();
        var authority = new EvaluationAuthority(
            identity.ProcessorId,
            identity.StreamId,
            new ObservationPosition(10),
            MachineState.Running,
            42,
            Policy("continuity/default", "1.0"),
            new StateProjectionAuthorityRevision(0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EvaluationAuthorityPublicationAccepted(
                authority,
                (EvaluationAuthorityPublicationDisposition)int.MaxValue));
    }

    private static EvaluationAuthorityPublication Publication(
        StateProjectionAuthorityRevision? expectedRevision,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) identity,
        ulong evaluatedThrough,
        MachineState machineState,
        ulong instanceId,
        CurrentStatePolicyReference policy) =>
        new(
            expectedRevision,
            identity.ProcessorId,
            identity.StreamId,
            new ObservationPosition(evaluatedThrough),
            machineState,
            instanceId,
            policy);

    private static CurrentStatePolicyReference Policy(string identity, string version) =>
        new(identity, version);

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId)
        Identity()
    {
        var machineId = MachineId.New();
        return (
            new ObservationProcessorId("machine-state"),
            new ObservationStreamId(machineId, "MTConnect:CNC-01"));
    }
}
