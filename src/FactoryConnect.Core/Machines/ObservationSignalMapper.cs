using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public static class ObservationSignalMapper
{
    public static MachineSignalValue? Map(
        MachineObservation observation,
        MachineSignalMapping mapping,
        MachineSignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(definition);

        if (!string.Equals(
                observation.Address,
                mapping.Source,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.Equals(
                mapping.SignalKey,
                definition.Key,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new MachineSignalValue
        {
            Key = definition.Key,
            Type = definition.Type,
            Value = observation.Value,
            Source = observation.Source,
            Quality = observation.Quality,
            Timestamp = observation.Timestamp,
        };
    }
}
