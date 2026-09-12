using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed class MachineSignalMappingProcessor : IObservationProcessor
{
    private readonly MachineSignalMappingConfiguration _configuration;
    private readonly IMappedMachineObservationSink _sink;
    private readonly IMappingCoverageAuthorityStore _authorityStore;

    public MachineSignalMappingProcessor(
        ObservationProcessorId processorId,
        MachineSignalMappingConfiguration configuration,
        IMappedMachineObservationSink sink,
        IMappingCoverageAuthorityStore authorityStore)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(authorityStore);

        _configuration = configuration with
        {
            Mappings = configuration.Mappings.ToArray(),
        };
        _sink = sink;
        _authorityStore = authorityStore;
        ProcessorId = processorId;
    }

    public ObservationProcessorId ProcessorId { get; }

    public async ValueTask ProcessAsync(
        IReadOnlyList<DurableMachineObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();

        if (observations.Count == 0)
        {
            return;
        }

        var streamId = observations[0].StreamId;
        var rawConsumedThrough = observations[0].Position;

        foreach (var durableObservation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (durableObservation.StreamId != streamId)
            {
                throw new InvalidOperationException(
                    "A mapping processor batch must belong to exactly one observation stream.");
            }

            if (durableObservation.Position > rawConsumedThrough)
            {
                rawConsumedThrough = durableObservation.Position;
            }
        }

        var expectedAuthority = await _authorityStore.ReadAsync(
            ProcessorId,
            streamId,
            cancellationToken);
        var mappedEvaluationInputHighWater =
            expectedAuthority?.MappedEvaluationInputHighWater;
        List<DurableMappedMachineObservation> mapped = [];

        foreach (var durableObservation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!MachineSignalMapper.TryMap(
                    durableObservation.Observation,
                    _configuration,
                    out var mappedObservation))
            {
                continue;
            }

            mapped.Add(
                new DurableMappedMachineObservation(
                    durableObservation.Position,
                    durableObservation.StreamId,
                    durableObservation.InstanceId,
                    durableObservation.Sequence,
                    mappedObservation!));

            if (mappedEvaluationInputHighWater is null ||
                durableObservation.Position > mappedEvaluationInputHighWater)
            {
                mappedEvaluationInputHighWater = durableObservation.Position;
            }
        }

        if (mapped.Count > 0)
        {
            await _sink.WriteAsync(mapped, cancellationToken);
        }

        await _authorityStore.CommitAsync(
            new MappingCoverageCommit(
                expectedAuthority,
                ProcessorId,
                streamId,
                rawConsumedThrough,
                mappedEvaluationInputHighWater),
            cancellationToken);
    }
}
