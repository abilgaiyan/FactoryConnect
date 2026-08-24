using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryObservationIngestionStoreConformanceTests :
    ObservationIngestionStoreConformanceTests
{
    protected override IObservationIngestionStore CreateStore() =>
        new InMemoryObservationIngestionStore();

    protected override int ReadObservationCount(
        IObservationIngestionStore store,
        ObservationStreamId streamId)
    {
        var inMemoryStore =
            Assert.IsType<InMemoryObservationIngestionStore>(store);

        return inMemoryStore.ReadObservations(streamId).Length;
    }
}
