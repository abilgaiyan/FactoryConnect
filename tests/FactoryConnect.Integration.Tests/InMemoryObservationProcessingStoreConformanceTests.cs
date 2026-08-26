using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryObservationProcessingStoreConformanceTests :
    ObservationProcessingStoreConformanceTests
{
    protected override IObservationIngestionStore CreateStore() =>
        new InMemoryObservationIngestionStore();
}
