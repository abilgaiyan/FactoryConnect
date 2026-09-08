using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryMachineShiftOccurrenceRosterStoreConformanceTests :
    MachineShiftOccurrenceRosterStoreConformanceTests
{
    protected override IMachineShiftOccurrenceRosterStore CreateStore() =>
        new InMemoryMachineShiftOccurrenceRosterStore();
}
