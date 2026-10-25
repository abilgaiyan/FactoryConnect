using System.Data;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

// Uses the existing disposable fixture. Execution remains gated with the other SQL tests.
public sealed partial class SqlContainmentHistoricalResultIntegrationTests
{
    private static readonly bool[] ScopePeriods = [false, true];

    [Fact]
    public async Task SharedInputsPreserveCanonicalResultsAcrossDefinitionsAndPeriodsAndRejectForgedClaims()
    {
        var seed = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction);
        var inputs = await scope.ReadAsync(seed.Cut, default);
        var aggregates = await scope.ReadAggregatesAsync(seed.Cut, default);
        Assert.Same(aggregates, await scope.ReadAggregatesAsync(seed.Cut, default));
        foreach (var day in ScopePeriods)
        foreach (var definition in BuiltInOperationalMetricDefinitions.All)
        {
            var key = Key(seed, day, definition.Id);
            var independent = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction, key, seed.Cut, default);
            var shared = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction, key, seed.Cut, default, scope);
            var bytes = OperationalMetricDependencySnapshotV2Codec.Encode(independent);
            Assert.Equal(bytes, OperationalMetricDependencySnapshotV2Codec.Encode(shared));
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, bytes, default, scope);
        }
        var performance = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction,
            Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), seed.Cut, default, scope);
        // Keep the claim internally canonical so rejection exercises historical
        // verification with warmed inputs, rather than the codec's ratio checks.
        var authority = performance.Authority!;
        var forgedAuthority = new ContainmentHistoricalAuthority(authority.CompletedReferenceRevision,
            ["forged-source"], authority.StandardRevisions);
        var forged = OperationalMetricDependencySnapshotV2Codec.Encode(Copy(performance, authority: forgedAuthority));
        _ = OperationalMetricDependencySnapshotV2Codec.Decode(forged);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, forged, default, scope));
        Assert.Same(inputs, await scope.ReadAsync(seed.Cut, default));
        Assert.Same(aggregates, await scope.ReadAggregatesAsync(seed.Cut, default));
        Assert.Equal(before, await Snapshot(connection, transaction));
    }

    [Fact]
    public async Task ScopeRejectsDifferentConnectionsTransactionsAndCompletedOwnerEvenOnWarmHits()
    {
        var seed = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction);
        _ = await scope.ReadAsync(seed.Cut, default);
        await using var other = TargetConnection(); await other.OpenAsync();
        await using var otherTransaction = other.BeginTransaction(IsolationLevel.Serializable);
        Assert.Throws<InvalidOperationException>(() => scope.RequireOwner(other, otherTransaction));
        await transaction.RollbackAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ReadAsync(seed.Cut, default));
        await using var next = connection.BeginTransaction(IsolationLevel.Serializable);
        Assert.Throws<InvalidOperationException>(() => scope.RequireOwner(connection, next));
        var fresh = new SqlContainmentHistoricalReadScope(connection, next);
        Assert.NotEmpty(await fresh.ReadAsync(seed.Cut, default));
    }

    [Fact]
    public async Task FirstAccessRejectsCorruptEvidenceAndReadCommittedTransactions()
    {
        var seed = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using (var weak = connection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            Assert.Throws<InvalidOperationException>(() => new SqlContainmentHistoricalReadScope(connection, weak));
            await weak.RollbackAsync();
        }
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        Assert.Equal(1, await Execute(connection, transaction, seed,
            "UPDATE dbo.MetricInputFact SET MetricValue=N'not-a-number' WHERE SourceQuantityEvidenceId=@Source AND MetricInputKey=N'quantity.part-count-increment';"));
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => scope.ReadAsync(seed.Cut, default));
        await transaction.RollbackAsync();
        await using var after = connection.BeginTransaction(IsolationLevel.Serializable);
        Assert.NotEmpty(await new SqlContainmentHistoricalReadScope(connection, after).ReadAsync(seed.Cut, default));
    }
}
