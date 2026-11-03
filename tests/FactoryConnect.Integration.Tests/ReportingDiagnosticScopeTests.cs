using System.Diagnostics;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class ReportingDiagnosticScopeTests
{
    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    public void TerminalOutcomeIsExplicit(string expected)
    {
        using var root = new Activity("diagnostic-test").Start();
        Activity? observed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == root.TraceId && activity.OperationName == "test.row") observed = activity;
            },
        };
        ActivitySource.AddActivityListener(listener);
        using (var scope = OperationalMetricReportingDiagnostics.Start("test.row"))
        {
            if (expected == "Cancelled") scope.Fail(new OperationCanceledException());
            if (expected == "Failed") scope.Fail(new InvalidDataException("authoritative failure"));
        }
        Assert.NotNull(observed);
        Assert.Equal(expected, observed.GetTagItem("diagnostic.outcome"));
    }

    [Fact]
    public void DiagnosticStopFailureDoesNotReplaceAuthoritativeFailure()
    {
        using var root = new Activity("diagnostic-failure-test").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == root.TraceId && activity.OperationName == "test.failure")
                    throw new InvalidOperationException("export failed");
            },
        };
        ActivitySource.AddActivityListener(listener);
        var authoritative = new InvalidDataException("verification failed");
        var observed = Assert.Throws<InvalidDataException>(() =>
        {
            using var scope = OperationalMetricReportingDiagnostics.Start("test.failure");
            try { throw authoritative; }
            catch (InvalidDataException error) { scope.Fail(error); throw; }
        });
        Assert.Same(authoritative, observed);
    }
    [Fact]
    public async Task SharedEvidenceEntryReportsOneMissOneLoadAndOneHit()
    {
        using var root = new Activity("diagnostic-cache-test").Start();
        var completed = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == root.TraceId && activity.OperationName == "containment.evidence-cache.lookup")
                    completed.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var machine = new FactoryConnect.Abstractions.MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
        var cut = new FactoryConnect.Abstractions.MetricAggregationCheckpoint(new("aggregation"), new(machine, "inputs"), new(534));
        var loads = 0;
        var cache = new SqlContainmentHistoricalReadScope.EvidenceCache((_, _) =>
        {
            loads++;
            return Task.FromResult(new SqlContainmentHistoricalContributionReader.ValidatedHistoricalContributions(
                [], FactoryConnect.Core.MetricInputContributionAggregator.Aggregate(cut.StreamId, [])));
        });
        var first = await cache.ReadAsync(cut, default);
        var second = await cache.ReadAsync(cut, default);
        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(2, completed.Count);
        Assert.Equal(1, completed.Count(a => a.GetTagItem("diagnostic.cache.loads") is 1));
        Assert.Equal(1, completed.Count(a => a.GetTagItem("diagnostic.cache.misses") is 1));
        Assert.Equal(1, completed.Count(a => a.GetTagItem("diagnostic.cache.hits") is 1));
    }

}
