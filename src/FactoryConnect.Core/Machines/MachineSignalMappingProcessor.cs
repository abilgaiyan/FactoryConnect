using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed class MachineSignalMappingProcessor : IObservationProcessor
{
    private readonly MachineSignalMappingConfiguration _configuration;
    private readonly IMappedMachineObservationSink _sink;

    public MachineSignalMappingProcessor(
        ObservationProcessorId processorId,
        MachineSignalMappingConfiguration configuration,
        IMappedMachineObservationSink sink)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sink);

        _configuration = configuration with
        {
            Mappings = configuration.Mappings.ToArray(),
        };
        _sink = sink;
        ProcessorId = processorId;
    }

    public ObservationProcessorId ProcessorId { get; }

    public async ValueTask ProcessAsync(
        IReadOnlyList<DurableMachineObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();

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
        }

        if (mapped.Count > 0)
        {
            await _sink.WriteAsync(mapped, cancellationToken);
        }
    }
}
