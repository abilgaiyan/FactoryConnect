namespace FactoryConnect.Abstractions;

public sealed record MachineStateActivityProjectionCommit
{
    public MachineStateActivityProjectionCommit(
        MachineStateActivityProjection? expectedProjection,
        MachineStateActivityProjection projection,
        IReadOnlyList<DurableMachineStateChangedEvent> stateChanges,
        IReadOnlyList<DurableMachineActivityPeriod> activityPeriods)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(stateChanges);
        ArgumentNullException.ThrowIfNull(activityPeriods);

        if (expectedProjection is not null &&
            (expectedProjection.ProcessorId != projection.ProcessorId ||
             expectedProjection.StreamId != projection.StreamId ||
             expectedProjection.Position >= projection.Position))
        {
            throw new ArgumentException(
                "The new projection must advance the same processor and stream.",
                nameof(projection));
        }

        ExpectedProjection = expectedProjection;
        Projection = projection;
        StateChanges = stateChanges.ToArray();
        ActivityPeriods = activityPeriods.ToArray();
    }

    public MachineStateActivityProjection? ExpectedProjection { get; }

    public MachineStateActivityProjection Projection { get; }

    public IReadOnlyList<DurableMachineStateChangedEvent> StateChanges { get; }

    public IReadOnlyList<DurableMachineActivityPeriod> ActivityPeriods { get; }
}
