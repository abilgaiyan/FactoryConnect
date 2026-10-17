using System.Text;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class B1ConcurrencyAcceptanceTests(ITestOutputHelper output)
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Reached(TaskCompletionSource signal) => await signal.Task.WaitAsync(TimeSpan.FromSeconds(20));

    [Fact]
    public async Task C1AdvancementAfterPublicationPrefixCannotLeakIntoBoundCut()
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var p = await f.NewPublisher("C1", true);
        var reached = Signal(); var release = Signal();
        p.AtStage = async (stage, _, _) => { if (stage == "prefix") { reached.TrySetResult(); await Reached(release); } };
        var publish = p.Publish();
        try
        {
            await Reached(reached);
            var later = await f.Append(MetricInputFactKeys.RunningDuration, 99, false);
            var next = new MetricAggregationCheckpoint(f.Aggregation, later.StreamId, later.Position);
            await f.AggregationStore.CommitAsync(new(f.Aggregation, f.Bound, next, [later]), CancellationToken.None);
            await f.Capture("C1: new aggregation committed while publisher paused after prefix");
            Assert.Equal(next, await f.AggregationStore.ReadCheckpointAsync(f.Aggregation, later.StreamId, CancellationToken.None));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("Published", await publish.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(f.Receipt(p.Id.Value, true), await p.Read());
        AssertPublished(await f.State(p));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C2ActualBeginOrCompleteNeverExposesPartialAuthority(bool complete)
    {
        await using var f = await B1ConcurrencyPrototype.Create(output, false);
        if (complete) { await f.Begin(); await f.Admissions.AdmitAsync(f.Aggregation, f.Bound.Position, f.Outcome); }
        await using var barrier = await f.Barrier("ProductionReferenceTimePublicationTransition", complete ? "UPDATE" : "INSERT");
        var writer = complete ? f.Transitions.CompleteAsync(f.Aggregation, f.Bound.Position) : f.Begin();
        var p = await f.NewPublisher("C2", true);
        Task<string>? publish = null;
        try
        {
            var writerSpid = await f.WaitFor(barrier.Spid, writer);
            publish = p.Publish();
            await f.WaitFor(writerSpid!.Value, publish);
            await f.Capture(complete ? "C2: terminal update uncommitted; validator waits" : "C2: pending claim insert uncommitted; validator waits");
        }
        finally { await barrier.Release(); }
        await writer.WaitAsync(TimeSpan.FromSeconds(30));
        if (complete)
        {
            Assert.Equal("Published", await publish!.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(f.Receipt(p.Id.Value, true), await p.Read()); AssertPublished(await f.State(p));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => publish!);
            Assert.Equal("HistoricalEvidenceUnavailable", failure.Message);
            AssertUnpublished(await f.State(p));
        }
    }

    [Fact]
    public async Task C3ActualAdmissionCannotConvertPendingAuthorityIntoMissingMetric()
    {
        await using var f = await B1ConcurrencyPrototype.Create(output, false);
        await f.Begin();
        await using (var barrier = await f.Barrier("ProductionReferenceTimeOutcome", "INSERT"))
        {
            var admission = f.Admissions.AdmitAsync(f.Aggregation, f.Bound.Position, f.Outcome);
            var p = await f.NewPublisher("C3-pending", true);
            try
            {
                await f.WaitFor(barrier.Spid, admission);
                var failure = await Assert.ThrowsAsync<InvalidDataException>(() => p.Publish());
                Assert.Equal("HistoricalEvidenceUnavailable", failure.Message);
                await f.Capture("C3: outcome inserted but admission uncommitted; pending authority rejected");
                AssertUnpublished(await f.State(p));
            }
            finally { await barrier.Release(); }
            await admission.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False((await f.Transitions.ReadAsync(f.Aggregation, f.Bound.Position))!.IsCompleted);
            var stillPending = await Assert.ThrowsAsync<InvalidDataException>(() => p.Publish());
            Assert.Equal("HistoricalEvidenceUnavailable", stillPending.Message);
        }
        await f.Transitions.CompleteAsync(f.Aggregation, f.Bound.Position);
        var completed = await f.NewPublisher("C3-completed", true);
        Assert.Equal("Published", await completed.Publish());
        Assert.Equal(f.Receipt(completed.Id.Value, true), await completed.Read());
        AssertPublished(await f.State(completed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C4ConcurrentReplayIsZeroWriteOrContentConflict(bool conflict)
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var first = await f.NewPublisher("C4", true);
        var reached = Signal(); var release = Signal(); var firstSpid = 0;
        first.AtStage = async (stage, c, _) => { if (stage == "prefix") { firstSpid = c.ServerProcessId; reached.TrySetResult(); await Reached(release); } };
        var winner = first.Publish();
        Task<string>? contender = null;
        try
        {
            await Reached(reached);
            var second = new B1ConcurrencyPrototype.Publisher(f, first.Id, first.Row, true);
            var payload = conflict ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(f.Receipt(first.Id.Value, true)).Replace("B1-COMPANY", "B1-CONFLICT", StringComparison.Ordinal)) : null;
            contender = second.Publish(payload);
            await f.WaitFor(firstSpid, contender);
        }
        finally { release.TrySetResult(); }
        await winner.WaitAsync(TimeSpan.FromSeconds(30));
        if (conflict) Assert.Equal("PublicationConflict", (await Assert.ThrowsAsync<InvalidDataException>(() => contender!)).Message);
        else Assert.Equal("Replayed", await contender!.WaitAsync(TimeSpan.FromSeconds(30)));
        var before = await f.State(first);
        first.AtStage = null;
        Assert.Equal("Replayed", await first.Publish());
        Assert.Equal(before, await f.State(first)); // Includes mutation-audit count, not only final row counts.
        AssertPublished(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C5ExistingReportingGatePreventsPartialPublication(bool readerFirst)
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var p = await f.NewPublisher("C5", true);
        var reached = Signal(); var release = Signal(); var holder = 0;
        p.AtStage = async (stage, c, _) =>
        {
            if (stage == (readerFirst ? "read-gate" : "evidence"))
            { holder = c.ServerProcessId; reached.TrySetResult(); await Reached(release); }
        };
        Task<byte[]> read;
        Task<string> publish;
        if (readerFirst)
        {
            read = p.Read(); await Reached(reached); publish = p.Publish();
        }
        else
        {
            publish = p.Publish(); await Reached(reached); read = p.Read();
        }
        try { await f.WaitFor(holder, readerFirst ? publish : read); await f.Capture("C5: reporting/publication gate in " + (readerFirst ? "reader-first" : "writer-first") + " order"); }
        finally { release.TrySetResult(); }
        var data = await read.WaitAsync(TimeSpan.FromSeconds(30));
        await publish.WaitAsync(TimeSpan.FromSeconds(30));
        if (readerFirst) Assert.Empty(data); else Assert.Equal(f.Receipt(p.Id.Value, true), data);
        p.AtStage = null;
        Assert.Equal(f.Receipt(p.Id.Value, true), await p.Read()); AssertPublished(await f.State(p));
    }

    [Fact]
    public async Task C6IndependentPublishersShareOneImmutableWitness()
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var first = await f.NewPublisher("C6-first", true); var second = await f.NewPublisher("C6-second", true);
        var reached = Signal(); var release = Signal(); var holder = 0;
        first.AtStage = async (stage, c, _) => { if (stage == "witness") { holder = c.ServerProcessId; reached.TrySetResult(); await Reached(release); } };
        var one = first.Publish(); await Reached(reached); var two = second.Publish();
        try { await f.WaitFor(holder, two); await f.Capture("C6: independent processor gates; actual observed resource determines serialization"); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(30));
        AssertPublished(await f.State(first)); AssertPublished(await f.State(second));
    }

    [Fact]
    public async Task C7ValidatorHoldsBoundCutWhileActualNeighborSourceWritersRun()
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var p = await f.NewPublisher("C7", true);
        var later = await f.Append(MetricInputFactKeys.PartCountIncrement, 2, null);
        var next = new MetricAggregationCheckpoint(f.Aggregation, later.StreamId, later.Position);
        var reached = Signal(); var release = Signal(); var holder = 0;
        p.AtStage = async (stage, c, _) => { if (stage == "validated") { holder = c.ServerProcessId; reached.TrySetResult(); await Reached(release); } };
        var publish = p.Publish(); await Reached(reached);
        var advancement = f.AggregationStore.CommitAsync(new(f.Aggregation, f.Bound, next, [later]), CancellationToken.None).AsTask();
        try { await f.WaitFor(holder, advancement, true); await f.Capture("C7: actual aggregation including R0/cut competes with held historical cut"); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(publish, advancement).WaitAsync(TimeSpan.FromSeconds(30));
        // Replay the legacy exact pair through the actual writer; neighboring target
        // begins/admission/completion use the actual claim-bound paths.
        var replay = f.Legacy.PublishAsync(f.Aggregation, f.Bound.Position, f.Outcome, CancellationToken.None);
        var begin = f.Transitions.BeginAsync(f.Aggregation, next.Position, f.Bound.Position, new(1), 1);
        await Task.WhenAll(replay, begin).WaitAsync(TimeSpan.FromSeconds(30));
        var source = Assert.Single(await f.AggregationStore.ReadProductionQuantityTransitionSourcesAsync(next, f.Machine, f.Bound.Position));
        var resolution = FactoryConnect.Core.ProductionStandardResolver.Resolve(source.Evidence, source.ShiftOccurrenceId, source.ProductionDayId, f.Standards);
        await using (var barrier = await f.Barrier("ProductionReferenceTimeOutcome", "INSERT"))
        {
            var admission = f.Admissions.AdmitAsync(f.Aggregation, next.Position, new(new(2), resolution));
            await f.WaitFor(barrier.Spid, admission);
            p.AtStage = null;
            var historical = p.Read();
            try { await f.WaitFor((await f.WaitFor(barrier.Spid, admission))!.Value, historical, true); await f.Capture("C7: actual neighboring R2 admission competes with old A4/R1 reconstruction"); }
            finally { await barrier.Release(); }
            await admission.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(f.Receipt(p.Id.Value, true), await historical.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        Assert.True((await f.Transitions.CompleteAsync(f.Aggregation, next.Position)).IsCompleted);
        p.AtStage = null;
        Assert.Equal(f.Receipt(p.Id.Value, true), await p.Read());
        await f.Capture("C7: neighboring transition terminal R2; original validator still reconstructs exact A4/R1");
        AssertPublished(await f.State(p));
    }

    [Theory]
    [InlineData("witness")]
    [InlineData("result")]
    [InlineData("references")]
    [InlineData("validated")]
    [InlineData("projection")]
    [InlineData("evidence")]
    [InlineData("manifest")]
    [InlineData("before-checkpoint")]
    [InlineData("checkpoint")]
    [InlineData("cancellation")]
    public async Task C8EveryPrototypeStageRollsBackAndRestartsCoherently(string stage)
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var p = await f.NewPublisher("C8", true);
        using var cancellation = new CancellationTokenSource();
        if (stage == "cancellation") p.AtStage = (point, _, _) => { if (point == "before-checkpoint") cancellation.Cancel(); return Task.CompletedTask; };
        else p.Fault = stage;
        if (stage == "checkpoint") Assert.Equal(51088, (await Assert.ThrowsAsync<SqlException>(() => p.Publish())).Number);
        else if (stage == "cancellation") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.Publish(token: cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => p.Publish());
        AssertUnpublished(await f.State(p));
        p.Fault = null; p.AtStage = null;
        Assert.Equal("Published", await p.Publish()); AssertPublished(await f.State(p));
        Assert.Equal(f.Receipt(p.Id.Value, true), await p.Read());
    }

    private static void AssertPublished(object[] state)
    { Assert.Equal(1, state[0]); Assert.Equal(1, state[1]); Assert.Equal(1, state[2]); Assert.Equal(1, state[3]); Assert.Equal(1, state[4]); Assert.Equal(2, state[5]); Assert.Equal(4m, state[6]); }
    private static void AssertUnpublished(object[] state)
    { Assert.Equal(0, state[0]); Assert.Equal(0, state[1]); Assert.Equal(0, state[2]); Assert.Equal(0, state[3]); Assert.Equal(0, state[4]); Assert.Equal(0, state[5]); Assert.Equal(1m, state[6]); }
}
