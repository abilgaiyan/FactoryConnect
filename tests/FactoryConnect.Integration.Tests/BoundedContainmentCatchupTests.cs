namespace FactoryConnect.Integration.Tests;

public sealed class BoundedContainmentCatchupTests
{
    [Theory]
    [InlineData(0)] [InlineData(10_001)]
    public void InvalidCycleLimitsAreRejected(int cycles) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainmentCatchupLimits(2150, cycles, TimeSpan.FromMinutes(1)));

    [Fact]
    public async Task LedgerPositionsMayHaveGapsAndStopExactlyAtCapturedTarget()
    {
        var remaining = new Queue<ulong>([610, 809, 2150]); ulong durable = 534;
        var progress = new List<ContainmentCatchupProgress>();
        var result = await BoundedContainmentCatchup.RunAsync(durable, Limits(2150, 10),
            (_, _) => Task.FromResult<ulong?>(remaining.Peek()), _ => { durable = remaining.Dequeue(); return Task.FromResult(10); },
            _ => Task.FromResult(durable), p => { progress.Add(p); return Task.CompletedTask; }, default);
        Assert.True(result.TargetReached); Assert.Equal(3, result.CompletedCycles); Assert.Empty(remaining);
        Assert.Equal(new ulong?[] { 610, 809, 2150 }, progress.Where(p => p.Phase == "Committed").Select(p => p.CandidateRevision));
    }

    [Fact]
    public async Task CycleLimitDoesNotSelectOrStartAnotherRevision()
    {
        ulong durable = 534; var reads = 0;
        var result = await BoundedContainmentCatchup.RunAsync(durable, Limits(2150, 2),
            (_, _) => { reads++; return Task.FromResult<ulong?>(durable + 10); }, _ => { durable += 10; return Task.FromResult(5); },
            _ => Task.FromResult(durable), Ignore, default);
        Assert.Equal("CycleLimitReached", result.Outcome); Assert.Equal(2, reads); Assert.Equal(554UL, result.LastDurableRevision);
        Assert.False(result.TargetReached); Assert.Null(result.FirstBlockingRevision);
    }

    [Fact]
    public async Task CandidateAboveCeilingNeverStartsRuntime()
    {
        var called = false;
        var result = await BoundedContainmentCatchup.RunAsync(534, Limits(600, 10), (_, _) => Task.FromResult<ulong?>(610),
            _ => { called = true; return Task.FromResult(1); }, _ => Task.FromResult(534UL), Ignore, default);
        Assert.False(called); Assert.Equal("CandidateOutsideBounds", result.Outcome); Assert.Equal(610UL, result.FirstBlockingRevision);
    }

    [Fact]
    public async Task FirstFailureIsRecordedWithoutSkippingOrRetrying()
    {
        var calls = 0;
        var result = await BoundedContainmentCatchup.RunAsync(534, Limits(2150, 10), (_, _) => Task.FromResult<ulong?>(610),
            _ => { calls++; throw new InvalidDataException("Synthetic historical failure"); }, _ => Task.FromResult(534UL), Ignore, default);
        Assert.Equal("Blocked", result.Outcome); Assert.Equal(610UL, result.FirstBlockingRevision);
        Assert.Equal(1, calls); Assert.Equal(534UL, result.LastDurableRevision); Assert.True(result.DurableReadbackSucceeded);
    }

    [Fact]
    public async Task InterruptedCommitIsReconciledAndIsNotMisreportedAsHistoricalBlock()
    {
        var result = await BoundedContainmentCatchup.RunAsync(534, Limits(2150, 10), (_, _) => Task.FromResult<ulong?>(610),
            _ => throw new IOException("Commit reply lost"), _ => Task.FromResult(610UL), Ignore, default);
        Assert.Equal("InterruptedAfterCommit", result.Outcome); Assert.Equal(610UL, result.LastDurableRevision);
        Assert.Null(result.FirstBlockingRevision);
    }

    [Fact]
    public async Task FailedReadbackDoesNotClaimDurableProgress()
    {
        var result = await BoundedContainmentCatchup.RunAsync(534, Limits(2150, 10), (_, _) => Task.FromResult<ulong?>(610),
            _ => throw new IOException("Commit reply lost"), _ => throw new IOException("Readback unavailable"), Ignore, default);
        Assert.Equal("ReconciliationFailed", result.Outcome); Assert.False(result.DurableReadbackSucceeded); Assert.Null(result.FirstBlockingRevision);
    }

    [Fact]
    public async Task ReplayedOrUnchangedCheckpointDoesNotCountAsCatchup()
    {
        var result = await BoundedContainmentCatchup.RunAsync(534, Limits(2150, 10), (_, _) => Task.FromResult<ulong?>(610),
            _ => Task.FromResult(0), _ => Task.FromResult(534UL), Ignore, default);
        Assert.Equal("UnexpectedProgress", result.Outcome); Assert.Equal(0, result.CompletedCycles); Assert.Equal(610UL, result.FirstBlockingRevision);
    }
    private static ContainmentCatchupLimits Limits(ulong target, int cycles) => new(target, cycles, TimeSpan.FromMinutes(1));
    private static Task Ignore(ContainmentCatchupProgress _) => Task.CompletedTask;
}
