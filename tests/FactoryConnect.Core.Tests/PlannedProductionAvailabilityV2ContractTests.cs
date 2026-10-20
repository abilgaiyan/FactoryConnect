using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

// Executable design oracle only. Synthetic complete timelines are not durable coverage proofs.
public sealed class PlannedProductionAvailabilityV2ContractTests
{
    private sealed record Fragment(string Id, int Start, int End, bool? Running, bool? Planned);
    private sealed record Outcome(string Status, decimal? Value, int Total, int Eligible, int Planned);

    [Theory]
    [InlineData(true, true, 1, 60, 60)]
    [InlineData(true, false, 0, 60, 0)]
    [InlineData(false, true, 0, 0, 0)]
    public void InsideOutsideAndCompleteZero(bool running, bool planned, int expected, int total, int eligible)
    {
        var result = Evaluate([new("a", 0, 60, running, planned)], 60);
        Assert.Equal(total, result.Total);
        Assert.Equal(eligible, result.Eligible);
        Assert.Equal(planned ? "Calculated" : "NoPlannedProduction", result.Status);
        Assert.Equal(planned ? (decimal?)expected : null, result.Value);
    }

    [Fact]
    public void CutoffRetainsTotalButExcludesLateRunning()
    {
        var result = Evaluate([new("before", 0, 30, true, true), new("after", 30, 60, true, false)], 60);
        Assert.Equal(new Outcome("Calculated", 1m, 60, 30, 30), result);
    }

    [Fact]
    public void BreakExcludesRunningWithoutLosingActivity()
    {
        var result = Evaluate([
            new("before", 0, 20, true, true), new("break", 20, 40, true, false),
            new("after", 40, 60, false, true)], 60);
        Assert.Equal(new Outcome("Calculated", 0.5m, 40, 20, 40), result);
    }

    [Theory]
    [InlineData(null, true, "IncompleteActivityCoverage")]
    [InlineData(true, null, "MissingEligibility")]
    public void UnknownDoesNotBecomeExcluded(bool? running, bool? planned, string reason)
    {
        Assert.Equal(reason, Evaluate([new("unknown", 0, 60, running, planned)], 60).Status);
    }

    [Fact]
    public void OutsideRunningWithCompleteNonRunningPlannedTimeCalculatesZero()
    {
        var result = Evaluate([new("planned", 0, 30, false, true), new("outside", 30, 60, true, false)], 60);
        Assert.Equal(new Outcome("Calculated", 0m, 30, 0, 30), result);
    }

    [Fact]
    public void MissingTimelineCannotProveZero()
    {
        Assert.Equal("IncompleteActivityCoverage",
            Evaluate([new("tail", 30, 60, false, true)], 60).Status);
        Assert.Equal("IncompleteActivityCoverage", Evaluate([], 60).Status);
    }

    [Fact]
    public void DuplicatesAndOverlapAreRejected()
    {
        Assert.Equal("InvalidIntervalEvidence", Evaluate([
            new("same", 0, 30, true, true), new("same", 30, 60, false, true)], 60).Status);
        Assert.Equal("InvalidIntervalEvidence", Evaluate([
            new("a", 0, 40, true, true), new("b", 30, 60, true, true)], 60).Status);
        Assert.Equal("InvalidIntervalEvidence", Evaluate([new("a", 0, 0, true, true)], 60).Status);
    }

    [Fact]
    public void ReorderingDoesNotChangePureOutcome()
    {
        Fragment[] input = [new("a", 0, 30, true, true), new("b", 30, 60, false, true)];
        Assert.Equal(Evaluate(input, 60), Evaluate(input.Reverse().ToArray(), 60));
        // This is determinism, not a durable publication replay test.
    }

    [Fact]
    public void LegacyOperandMappingRemainsTotalRunning()
    {
        Assert.Equal(MetricInputFactKeys.RunningDuration,
            OperationalMetricComponentKeyMapping.AggregateKey(MetricInputKeys.ActualProductionTime));
        Assert.Equal(MetricInputFactKeys.PlannedProductionDuration,
            OperationalMetricComponentKeyMapping.AggregateKey(MetricInputKeys.PlannedOperatingTime));
    }

    // Segments have already been split at all authoritative boundaries.
    // Full synthetic coverage is checked here; no production coverage claim follows.
    private static Outcome Evaluate(IReadOnlyList<Fragment> input, int scopeEnd)
    {
        Outcome Failure(string reason) => new(reason, null, 0, 0, 0);
        var ordered = input.OrderBy(x => x.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (scopeEnd <= 0 || ordered.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length ||
            ordered.Any(x => x.Start < 0 || x.End > scopeEnd || x.End <= x.Start))
            return Failure("InvalidIntervalEvidence");
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Start < ordered[i - 1].End) return Failure("InvalidIntervalEvidence");
        if (ordered.Any(x => x.Planned is null)) return Failure("MissingEligibility");
        if (ordered.Length == 0 || ordered[0].Start != 0 || ordered[^1].End != scopeEnd ||
            ordered.Any(x => x.Running is null)) return Failure("IncompleteActivityCoverage");
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Start != ordered[i - 1].End) return Failure("IncompleteActivityCoverage");
        var total = ordered.Where(x => x.Running == true).Sum(x => x.End - x.Start);
        var eligible = ordered.Where(x => x.Running == true && x.Planned == true).Sum(x => x.End - x.Start);
        var planned = ordered.Where(x => x.Planned == true).Sum(x => x.End - x.Start);
        return planned == 0 ? new("NoPlannedProduction", null, total, eligible, planned)
            : new("Calculated", (decimal)eligible / planned, total, eligible, planned);
    }
}
