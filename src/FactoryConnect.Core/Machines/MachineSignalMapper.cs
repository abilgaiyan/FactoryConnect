using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public static class MachineSignalMapper
{
    public static bool TryMap(
        MachineObservation observation,
        MachineSignalMappingConfiguration configuration,
        out MappedMachineObservation? mappedObservation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(configuration);

        if (observation.MachineId != configuration.MachineId)
        {
            throw new ArgumentException(
                "Machine observation does not match the signal mapping configuration scope.",
                nameof(observation));
        }

        var matches = configuration.Mappings
            .Where(mapping =>
                string.Equals(mapping.Source, observation.Source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mapping.Address, observation.Address, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            mappedObservation = null;
            return false;
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple signal mappings exist for source '{observation.Source}' and address '{observation.Address}'.");
        }

        var mapping = matches[0];

        if (mapping.Type != observation.Type)
        {
            throw new InvalidOperationException(
                $"Signal mapping type '{mapping.Type}' does not match observation type '{observation.Type}'.");
        }

        mappedObservation = new MappedMachineObservation
        {
            MachineId = observation.MachineId,
            SignalKey = mapping.SignalKey,
            Type = mapping.Type,
            Value = ApplyMappingValue(observation.Value, mapping),
            Source = observation.Source,
            Address = observation.Address,
            Quality = observation.Quality,
            Timestamp = observation.Timestamp,
        };

        return true;
    }

    private static object? ApplyMappingValue(
        object? value,
        MachineSignalMappingDefinition mapping)
    {
        if (!mapping.Invert)
        {
            return value;
        }

        if (mapping.Type != SignalType.Digital || value is not bool digitalValue)
        {
            throw new InvalidOperationException(
                "Signal inversion is supported only for digital Boolean observations.");
        }

        return !digitalValue;
    }
}
