namespace FactoryConnect.Abstractions;

/// <summary>
/// Describes the resulting portable mapping coverage after one successfully
/// processed durable raw-observation batch.
/// </summary>
public sealed record MappingCoverageCommit
{
    public MappingCoverageCommit(
        MappingCoverageAuthority? expectedAuthority,
        ObservationProcessorId mappingProcessorId,
        ObservationStreamId observationStreamId,
        ObservationPosition rawConsumedThrough,
        ObservationPosition? mappedEvaluationInputHighWater)
    {
        ArgumentNullException.ThrowIfNull(mappingProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        ArgumentNullException.ThrowIfNull(rawConsumedThrough);

        if (mappedEvaluationInputHighWater is not null &&
            mappedEvaluationInputHighWater > rawConsumedThrough)
        {
            throw new ArgumentException(
                "Mapped evaluation input high-water cannot exceed raw consumed coverage.",
                nameof(mappedEvaluationInputHighWater));
        }

        if (expectedAuthority is not null)
        {
            if (expectedAuthority.MappingProcessorId != mappingProcessorId ||
                expectedAuthority.ObservationStreamId != observationStreamId)
            {
                throw new ArgumentException(
                    "Expected mapping authority must belong to the same processor and stream.",
                    nameof(expectedAuthority));
            }

            if (rawConsumedThrough < expectedAuthority.RawConsumedThrough)
            {
                throw new ArgumentException(
                    "Raw consumed coverage cannot move backwards.",
                    nameof(rawConsumedThrough));
            }

            if (expectedAuthority.MappedEvaluationInputHighWater is not null &&
                (mappedEvaluationInputHighWater is null ||
                 mappedEvaluationInputHighWater < expectedAuthority.MappedEvaluationInputHighWater))
            {
                throw new ArgumentException(
                    "Mapped evaluation input high-water cannot move backwards.",
                    nameof(mappedEvaluationInputHighWater));
            }
        }

        ExpectedAuthority = expectedAuthority;
        MappingProcessorId = mappingProcessorId;
        ObservationStreamId = observationStreamId;
        RawConsumedThrough = rawConsumedThrough;
        MappedEvaluationInputHighWater = mappedEvaluationInputHighWater;
    }

    public MappingCoverageAuthority? ExpectedAuthority { get; }

    public ObservationProcessorId MappingProcessorId { get; }

    public ObservationStreamId ObservationStreamId { get; }

    public ObservationPosition RawConsumedThrough { get; }

    public ObservationPosition? MappedEvaluationInputHighWater { get; }
}
