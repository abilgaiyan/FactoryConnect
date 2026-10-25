using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

public sealed partial class SqlContainmentHistoricalResultIntegrationTests
{
    [Fact]
    public async Task SetBasedReferenceChainMatchesPointReadsAcrossManyDistinctHistoricalCuts()
    {
        var seed = await SeedAsync();
        var cuts = new List<MetricAggregationCheckpoint> { seed.Cut };
        var inputStore = new SqlServerMetricInputStore(TargetConnectionString);
        var aggregation = new SqlServerMetricAggregationStore(TargetConnectionString);
        var convergence = new SqlServerProductionReferenceTimeConvergenceCoordinator(TargetConnectionString);
        var pointReader = new SqlServerProductionReferenceTimePublicationTransitionStore(TargetConnectionString);
        var expected = new List<ProductionReferenceTimePublicationTransition>
        {
            (await pointReader.ReadAsync(seed.Cut.ProcessorId, seed.Cut.Position, default))!
        };
        for (var i = 0; i < 23; i++)
        {
            var fact = seed.Quantity!.Fact with
            {
                Id = new MetricInputFactId(Guid.NewGuid().ToString()),
                SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(Guid.NewGuid().ToString())
            };
            var input = await inputStore.AppendAsync(new(seed.Cut.StreamId, fact, seed.Shift, seed.Day), default);
            var previous = cuts[^1];
            var next = new MetricAggregationCheckpoint(seed.Cut.ProcessorId, seed.Cut.StreamId, input.Position);
            await aggregation.CommitAsync(new(seed.Cut.ProcessorId, previous, next, [input]), default);
            expected.Add(await convergence.ConvergeAsync(next, seed.Machine, previous.Position));
            cuts.Add(next);
        }
        await using var connection = TargetConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var processor = await SqlServerProductionReferenceTimePublicationTransitionStore.ReadProcessorRowIdAsync(
            connection, transaction, seed.Cut.ProcessorId, default);
        var historicalReads = new SqlContainmentHistoricalReadScope(connection, transaction);
        // Read out of order, so accidentally reusing latest-machine authority cannot pass.
        foreach (var index in Enumerable.Range(0, cuts.Count).OrderBy(i => (i * 7) % cuts.Count))
        {
            var cut = cuts[index];
            var chain = await SqlContainmentReferenceTimeAuthorityValidator.ReadTransitionChainAsync(
                connection, transaction, cut, processor, default);
            Assert.Equal(expected.Take(index + 1), chain);
            var point = await SqlServerProductionReferenceTimePublicationTransitionStore.ReadAsync(
                connection, transaction, cut.ProcessorId, processor, cut.Position, false, default);
            Assert.Equal(point, chain[^1]);
            var document = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction,
                Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), cut, default, historicalReads);
            Assert.Equal((long)index + 1, document.Authority!.CompletedReferenceRevision);
            Assert.Equal(index + 1, document.Authority.SourceQuantityIds.Count);
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction,
                OperationalMetricDependencySnapshotV2Codec.Encode(document), default, historicalReads);
        }
        // A damaged middle revision must fail even when the requested terminal transition is intact.
        foreach (var tableAndColumn in new[]
        {
            ("ProductionReferenceTimePublicationTransition", "TargetMetricAggregationPosition"),
            ("ProductionReferenceTimePublicationCut", "MetricAggregationPosition")
        })
        {
            transaction.Save("before_damage");
            await using var damage = connection.CreateCommand();
            damage.Transaction = transaction;
            damage.CommandText = $"DELETE FROM dbo.{tableAndColumn.Item1} WHERE MetricAggregationProcessorRowId=@P AND {tableAndColumn.Item2}=@A;";
            damage.Parameters.Add("@P", SqlDbType.BigInt).Value = processor;
            damage.Parameters.Add(SqlServerUInt64.CreateParameter("@A", cuts[10].Position.Value));
            Assert.True(await damage.ExecuteNonQueryAsync() > 0);
            await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() =>
                SqlContainmentReferenceTimeAuthorityValidator.ReadTransitionChainAsync(connection, transaction, cuts[^1], processor, default));
            transaction.Rollback("before_damage");
        }
        await transaction.RollbackAsync();
    }
}
