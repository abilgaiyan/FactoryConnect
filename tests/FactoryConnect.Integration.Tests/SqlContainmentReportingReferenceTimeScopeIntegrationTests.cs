using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed partial class SqlContainmentHistoricalResultIntegrationTests
{
    [Fact]
    public async Task CutEvidenceReuseNeverReusesPeriodSelectionOrMissingDurationResult()
    {
        var seed = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        var first = new OperationalMetricPeriodId.Shift(seed.Shift);
        var absent = new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(seed.Shift.SiteId,
            seed.Shift.ShiftScheduleAssignmentId, new ShiftId("no-sources"),
            seed.Shift.EndsAtUtc, seed.Shift.EndsAtUtc.AddHours(8)));
        var present = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, first, default, scope);
        var missing = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, absent, default, scope);
        var again = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, first, default, scope);
        Assert.NotNull(present.Aggregate);
        Assert.Null(missing.Aggregate);
        Assert.Empty(missing.Binding.SourceQuantityIds);
        Assert.Equal(present.Aggregate, again.Aggregate);
        Assert.Equal(present.Binding.SourceQuantityIds, again.Binding.SourceQuantityIds);
        Assert.NotSame(present, again);
        Assert.Equal(1, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(2, reference.CacheHits);
        Assert.Equal(3, reference.PeriodEvaluations);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-standard")]
    public async Task ValidatedMissingAuthorityHistoryStillRequiresEveryCanonicalEnvelopeComparison(string mode)
    {
        var seed = await SeedAsync(mode);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        foreach (var definition in new[] { BuiltInOperationalMetricDefinitions.PerformanceId, BuiltInOperationalMetricDefinitions.OeeId })
        {
            var key = Key(seed, false, definition);
            var expected = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction, key, seed.Cut, default);
            var bytes = OperationalMetricDependencySnapshotV2Codec.Encode(expected);
            var actual = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, bytes, default, scope);
            Assert.Equal(bytes, OperationalMetricDependencySnapshotV2Codec.Encode(actual));
        }
        Assert.Equal(2, scope.RootReconstructionInvocations);
        Assert.Equal(1, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(1, reference.CacheHits);
        Assert.Equal(2, reference.PeriodEvaluations);
    }

    [Theory]
    [InlineData("empty-pending")]
    [InlineData("empty-cut")]
    [InlineData("outcome")]
    [InlineData("standard")]
    public async Task UnrelatedWarmEvidenceCannotCertifyIncompleteHistory(string missing)
    {
        var bad = await SeedAsync(missing.StartsWith("empty", StringComparison.Ordinal) ? "empty" : "resolved");
        var good = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        // Damage the target before creating the read-only scope. No authority is changed after admission.
        await Execute(connection, transaction, bad, missing switch
        {
            "empty-pending" => "UPDATE dbo.ProductionReferenceTimePublicationTransition SET State=0, FinalProductionReferenceTimeRevision=NULL WHERE MetricAggregationProcessorRowId=@P;",
            "empty-cut" => "DELETE FROM dbo.ProductionReferenceTimePublicationCut WHERE MetricAggregationProcessorRowId=@P;",
            "outcome" => "DELETE FROM dbo.ProductionReferenceTimeOutcome WHERE MetricAggregationProcessorRowId=@P;",
            _ => "DELETE FROM dbo.ProductionStandardVersion WHERE VersionId=@Standard;",
        });
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        // Warm the unrelated valid cut. Full-cut checks still apply.
        // A missing global standard version may invalidate later standard cuts as well, so do not
        // load that unrelated cut for the global-standard corruption case.
        if (missing != "standard")
            _ = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, good.Cut,
                Key(good, false, BuiltInOperationalMetricDefinitions.PerformanceId).PeriodId, default, scope);
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() =>
                SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, bad.Cut,
                    Key(bad, false, BuiltInOperationalMetricDefinitions.PerformanceId).PeriodId, default, scope));
        Assert.Equal(missing == "standard" ? 2 : 3, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(0, reference.CacheHits);
    }

    [Fact]
    public async Task ScopeRejectsWrongCutEvidenceAndOverlappingLoadsBeforeAdmission()
    {
        var first = await SeedAsync();
        var other = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var historical = new SqlContainmentHistoricalReadScope(connection, transaction);
        var wrong = await SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence.LoadAsync(
            connection, transaction, other.Cut, historical, default);
        var correct = await SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence.LoadAsync(
            connection, transaction, first.Cut, historical, default);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reference.ReadAsync(connection, transaction,
            first.Cut, _ => Task.FromResult(wrong), default));
        var release = new TaskCompletionSource<SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reference.ReadAsync(connection, transaction, first.Cut, _ => release.Task, default);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => reference.ReadAsync(connection, transaction,
                first.Cut, _ => Task.FromResult(correct), default));
        }
        finally { release.TrySetResult(correct); }
        Assert.Same(correct, await pending);
        Assert.Equal(2, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(0, reference.CacheHits);
    }

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
        Assert.Equal(4, reference.PeriodEvaluations);
    }

    [Fact]
    public async Task ReportingSharesCutEvidenceAcrossPeriodsButSeparatesCompleteSourceCuts()
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
            var repeated = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope);
            Assert.NotSame(actual, repeated);
            Assert.Equal(actual.Aggregate, repeated.Aggregate);
            Assert.Equal(actual.Binding.SourceQuantityIds, repeated.Binding.SourceQuantityIds);
        }
        Assert.Equal(2, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(6, reference.CacheHits);
        Assert.Equal(8, reference.PeriodEvaluations);
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
            var repeated = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, cut, period, default, scope);
            Assert.NotSame(actual, repeated);
            Assert.Equal(actual.Aggregate, repeated.Aggregate);
        }
        Assert.Equal(2, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(2, reference.CacheHits);
    }

    [Theory]
    [InlineData("missing-identity")]
    [InlineData("missing-standard")]
    [InlineData("ambiguous")]
    [InlineData("empty")]
    public async Task ReportingReusesValidatedUnresolvedOrEmptyEvidenceButDerivesMissingDurationIndependently(string mode)
    {
        var seed = await SeedAsync(mode);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var reference = new SqlContainmentReportingReferenceTimeScope(connection, transaction);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction, reference);
        var period = Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId).PeriodId;
        Assert.Null((await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope)).Aggregate);
        Assert.Null((await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope)).Aggregate);
        Assert.Equal(1, reference.ReferenceTimeAuthorityValidationExecutions);
        Assert.Equal(1, reference.CacheHits);
        Assert.Equal(2, reference.PeriodEvaluations);
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
            seed.Cut, _ => throw new InvalidOperationException("Must not load foreign authority."), default));
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
            var validated = await SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence.LoadAsync(
                connection, transaction, seed.Cut, new SqlContainmentHistoricalReadScope(connection, transaction), default);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var cancellation = new CancellationTokenSource();
                Task<SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence> Validate(CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    if (mode == "failure") throw new InvalidOperationException("Injected validation failure.");
                    cancellation.Cancel();
                    return Task.FromResult(validated);
                }
                if (mode == "failure")
                    await Assert.ThrowsAsync<InvalidOperationException>(() => reference.ReadAsync(connection, transaction,
                        seed.Cut, Validate, default));
                else
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reference.ReadAsync(connection, transaction,
                        seed.Cut, Validate, cancellation.Token));
            }
            _ = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction, seed.Cut, period, default, scope);
        }
        Assert.Equal(0, reference.CacheHits);
        Assert.Equal(mode == "corruption" ? 2 : 3, reference.ReferenceTimeAuthorityValidationExecutions);
    }
}
