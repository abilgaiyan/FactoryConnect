using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed partial class SqlContainmentHistoricalResultIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportingReusesValidatedAuthorityForPerformanceAndOeeButStillReconstructsAndComparesEveryEnvelope(bool unavailable)
    {
        var seed = await SeedAsync(unavailable: unavailable);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        foreach (var definition in new[] { BuiltInOperationalMetricDefinitions.PerformanceId, BuiltInOperationalMetricDefinitions.OeeId })
        {
            var key = Key(seed, false, definition);
            var independent = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction, key, seed.Cut, default);
            var bytes = OperationalMetricDependencySnapshotV2Codec.Encode(independent);
            var actual = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, bytes, default, scope);
            Assert.Equal(bytes, OperationalMetricDependencySnapshotV2Codec.Encode(actual));
        }
        Assert.Equal(2, scope.RootReconstructionInvocations);
        Assert.Equal(1, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(1, reference.CacheHits);
        var performance = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction,
            Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), seed.Cut, default, scope);
        var authority = performance.Authority!;
        var forged = OperationalMetricDependencySnapshotV2Codec.Encode(Copy(performance,
            authority: new ContainmentHistoricalAuthority(authority.CompletedReferenceRevision, ["forged-source"], authority.StandardRevisions)));
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, forged, default, scope));
        Assert.Equal(4, scope.RootReconstructionInvocations);
        Assert.Equal(1, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(3, reference.CacheHits);
    }

    [Fact]
    public async Task ReportingAuthoritySeparatesExactPeriodsAndCompleteSourceCuts()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        foreach (var seed in new[] { first, second })
        foreach (var day in ScopePeriods)
        {
            var period = Key(seed, day, BuiltInOperationalMetricDefinitions.PerformanceId).PeriodId;
            var expected = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default);
            var actual = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope);
            Assert.Equal(expected.Aggregate, actual.Aggregate);
            Assert.Equal(expected.Binding.SourceQuantityIds, actual.Binding.SourceQuantityIds);
            Assert.Same(actual, await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope));
        }
        Assert.Equal(4, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(4, reference.CacheHits);
    }

    [Fact]
    public async Task ReportingAuthoritySeparatesHistoricalRevisionsOfSameSource()
    {
        var (first, next) = await CreatePublicationAdvanceEvidenceAsync();
        var prior = ContainmentArchiveBatch.Preflight(first);
        var current = ContainmentArchiveBatch.Preflight(next);
        Assert.NotEqual(prior.Cut, current.Cut);
        var period = prior.Results[0].Document.Key.PeriodId;
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        foreach (var cut in new[] { prior.Cut, current.Cut })
        {
            var expected = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, cut, period, default);
            var actual = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, cut, period, default, scope);
            Assert.Equal(expected.Aggregate, actual.Aggregate);
            Assert.Equal(expected.Binding.CompletedReferenceRevision, actual.Binding.CompletedReferenceRevision);
            Assert.Same(actual, await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, cut, period, default, scope));
        }
        Assert.Equal(2, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(2, reference.CacheHits);
    }

    [Theory]
    [InlineData("missing-identity")]
    [InlineData("missing-standard")]
    [InlineData("ambiguous")]
    [InlineData("empty")]
    public async Task ReportingDoesNotCacheMissingOrInsufficientReferenceTime(string mode)
    {
        var seed = await SeedAsync(mode);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        var period = Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId).PeriodId;
        Assert.Null((await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope)).Aggregate);
        Assert.Null((await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope)).Aggregate);
        Assert.Equal(2, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(0, reference.CacheHits);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("dispose")]
    public async Task ReportingAuthorityChecksCancellationAndOwnerLifetimeEvenOnHits(string completion)
    {
        var seed = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        var period = Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId).PeriodId;
        _ = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, cancellation.Token, scope));
        Assert.Equal(0, reference.CacheHits);
        await using var other = TargetConnection(); await other.OpenAsync();
        await using var otherTransaction = other.BeginTransaction(IsolationLevel.Serializable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reference.ReadAsync(other, otherTransaction,
            seed.Cut, period, default, _ => throw new InvalidOperationException("Must not load foreign authority.")));
        if (completion == "commit") await transaction.CommitAsync();
        else if (completion == "rollback") await transaction.RollbackAsync();
        else await transaction.DisposeAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope));
        await using var next = connection.BeginTransaction(IsolationLevel.Serializable);
        var stale = new SqlContainmentHistoricalReadScope(connection, next, reference);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, next, seed.Cut, period, default, stale));
    }

    [Theory]
    [InlineData("corruption")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task ReportingAuthorityDoesNotMemoizeValidationFailures(string mode)
    {
        var seed = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        var period = Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId).PeriodId;
        if (mode == "corruption")
        {
            await Execute(connection, transaction, seed,
                "UPDATE dbo.ProductionReferenceTimeOutcome SET IdealProductionDurationSeconds=5 WHERE MetricAggregationProcessorRowId=@P;");
            for (var attempt = 0; attempt < 2; attempt++)
                await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
                    SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope));
        }
        else
        {
            var validated = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(
                connection, transaction, seed.Cut, period, default);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var cancellation = new CancellationTokenSource();
                Task<SqlContainmentReferenceTimeAuthorityValidator.VerifiedReferenceTime> Validate(CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    if (mode == "failure") throw new InvalidOperationException("Injected validation failure.");
                    cancellation.Cancel();
                    return Task.FromResult(validated);
                }
                if (mode == "failure")
                    await Assert.ThrowsAsync<InvalidOperationException>(() => reference.ReadAsync(connection, transaction,
                        seed.Cut, period, default, Validate));
                else
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reference.ReadAsync(connection, transaction,
                        seed.Cut, period, cancellation.Token, Validate));
            }
            _ = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope);
        }
        Assert.Equal(0, reference.CacheHits);
        Assert.Equal(mode == "corruption" ? 2 : 3, reference.ReferenceTimeAuthorityValidationExecutions);
    }
}
