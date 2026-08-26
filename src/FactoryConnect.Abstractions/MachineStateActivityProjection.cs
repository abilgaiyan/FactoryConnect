namespace FactoryConnect.Abstractions;

public sealed record MachineStateActivityProjection
{
    public MachineStateActivityProjection(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ObservationPosition position,
        IReadOnlyList<MachineSignalValue> signals,
        MachineState state,
        MachineState? activeState,
        DateTimeOffset? activeStartedAt)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(signals);

        if (activeState.HasValue != activeStartedAt.HasValue)
        {
            throw new ArgumentException(
                "Active state and start time must either both be present or both be absent.",
                nameof(activeState));
        }

        ProcessorId = processorId;
        StreamId = streamId;
        Position = position;
        Signals = signals.ToArray();
        State = state;
        ActiveState = activeState;
        ActiveStartedAt = activeStartedAt;
    }

    public ObservationProcessorId ProcessorId { get; }

    public ObservationStreamId StreamId { get; }

    public ObservationPosition Position { get; }

    public IReadOnlyList<MachineSignalValue> Signals { get; }

    public MachineState State { get; }

    public MachineState? ActiveState { get; }

    public DateTimeOffset? ActiveStartedAt { get; }
}
