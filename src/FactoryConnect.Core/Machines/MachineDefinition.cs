using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed record MachineDefinition(
    MachineId Id,
    string Name,
    string? Line = null);
