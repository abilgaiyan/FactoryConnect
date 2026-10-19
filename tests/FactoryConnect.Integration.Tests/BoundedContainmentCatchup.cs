using System.Diagnostics;

namespace FactoryConnect.Integration.Tests;

internal sealed record ContainmentCatchupLimits
{
    internal ContainmentCatchupLimits(ulong target, int maximumCycles, TimeSpan timeBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(target, 534UL);
        if (maximumCycles is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumCycles));
        if (timeBudget <= TimeSpan.Zero || timeBudget > TimeSpan.FromHours(2)) throw new ArgumentOutOfRangeException(nameof(timeBudget));
        Target = target; MaximumCycles = maximumCycles; TimeBudget = timeBudget;
    }
    internal ulong Target { get; }
    internal int MaximumCycles { get; }
    internal TimeSpan TimeBudget { get; }
}

internal sealed record ContainmentCatchupProgress(string Phase, ulong LastDurableRevision, ulong? CandidateRevision,
    int CompletedCycles, int? ProjectionCount, long ElapsedMilliseconds);
internal sealed record ContainmentCatchupResult(string Outcome, ulong StartRevision, ulong LastDurableRevision,
    ulong TargetRevision, int CompletedCycles, ulong? CandidateRevision, ulong? FirstBlockingRevision,
    string? ErrorType, bool DurableReadbackSucceeded)
{
    public bool TargetReached => Outcome == "TargetReached";
}

/// <summary>Test/rehearsal orchestration only. Revision positions are ledger identities, not a dense sequence.</summary>
internal static class BoundedContainmentCatchup
{
    internal static async Task<ContainmentCatchupResult> RunAsync(ulong start, ContainmentCatchupLimits limits,
        Func<ulong, CancellationToken, Task<ulong?>> readNext,
        Func<CancellationToken, Task<int>> runCycle,
        Func<CancellationToken, Task<ulong>> readDurable,
        Func<ContainmentCatchupProgress, Task> record, CancellationToken cancellationToken)
    {
        if (start < 534 || start > limits.Target) throw new ArgumentOutOfRangeException(nameof(start));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(limits.TimeBudget);
        var clock = Stopwatch.StartNew(); var durable = start; var cycles = 0; ulong? candidate = null;
        while (durable < limits.Target && cycles < limits.MaximumCycles)
        {
            candidate = null;
            var recording = false;
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                candidate = await readNext(durable, budget.Token);
                if (candidate is null) return Result("SourceExhaustedBeforeTarget", null, null, true);
                if (candidate <= durable || candidate > limits.Target) return Result("CandidateOutsideBounds", candidate, null, true);
                recording = true;
                await record(new("BeforeCycle", durable, candidate, cycles, null, clock.ElapsedMilliseconds));
                recording = false;
                var count = await runCycle(budget.Token);
                var observed = await readDurable(budget.Token);
                if (observed != candidate || count <= 0)
                {
                    durable = observed;
                    return Result("UnexpectedProgress", candidate, null, true);
                }
                durable = observed; cycles++;
                recording = true;
                await record(new("Committed", durable, candidate, cycles, count, clock.ElapsedMilliseconds));
            }
            catch (Exception error)
            {
                // A failed/cancelled commit can have an ambiguous client outcome. Always reconcile;
                // never retry automatically or call the candidate a historical blocker after it committed.
                var known = true;
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    durable = await readDurable(cleanup.Token);
                }
                catch { known = false; }
                var cancelled = error is OperationCanceledException;
                var afterCommit = known && candidate is not null && durable >= candidate;
                var outcome = recording ? "EvidenceWriteFailed" : !known ? "ReconciliationFailed" : afterCommit ? "InterruptedAfterCommit" : cancelled
                    ? (cancellationToken.IsCancellationRequested ? "Cancelled" : "TimeBudgetReached") : "Blocked";
                if (!recording) await record(new(outcome, durable, candidate, cycles, null, clock.ElapsedMilliseconds));
                return Result(outcome, known && !recording && !cancelled && !afterCommit ? candidate : null, error.GetType().FullName, known);
            }
        }
        return Result(durable == limits.Target ? "TargetReached" : "CycleLimitReached", null, null, true);

        ContainmentCatchupResult Result(string outcome, ulong? blocked, string? error, bool known) =>
            new(outcome, start, durable, limits.Target, cycles, candidate, blocked, error, known);
    }
}
