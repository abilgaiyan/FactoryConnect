using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Testing;

namespace FactoryConnect.Core.Tests;

public sealed class InMemoryOperationalMetricReportingQueryProviderConformanceTests :
    OperationalMetricReportingQueryProviderConformanceTests
{
    protected override ValueTask<IOperationalMetricReportingQueryProviderFixture>
        CreateProviderAsync() =>
        ValueTask.FromResult<IOperationalMetricReportingQueryProviderFixture>(
            new InMemoryProviderFixture());

    private sealed class InMemoryProviderFixture :
        IOperationalMetricReportingQueryProviderFixture
    {
        private readonly InMemoryOperationalMetricProjectionStore _store = new();

        public IOperationalMetricReportingQueryProvider Provider => _store;

        public async Task SeedAsync(params OperationalMetricProjection[] projections)
        {
            ArgumentNullException.ThrowIfNull(projections);

            foreach (var group in projections.GroupBy(static projection => projection.ProcessorId))
            {
                var snapshot = group.ToArray();
                if (snapshot.Length == 0)
                {
                    continue;
                }

                var revision = snapshot[0].SourceRevision;
                await _store.CommitAsync(
                    new OperationalMetricProjectionCommit(
                        group.Key,
                        null,
                        new OperationalMetricProjectionCheckpoint(
                            group.Key,
                            revision,
                            new OperationalMetricProjectionBatchManifest(
                                snapshot.Select(static projection => projection.Key))),
                        snapshot),
                    CancellationToken.None);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
