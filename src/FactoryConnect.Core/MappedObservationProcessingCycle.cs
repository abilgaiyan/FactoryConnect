using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core;

public sealed record MappedObservationProcessingCycle(
    MappedObservationReadBatch Batch,
    MachineStateActivityChronologyHold? Hold);
