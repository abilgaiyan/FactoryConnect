using System.Text.Json;
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task C2ActualBeginOrCompleteNeverExposesPartialAuthority(bool complete, bool validatorFirst)
    {
        await using var f = await B1ConcurrencyPrototype.Create(output, false);
        if (complete) { await f.Begin(); await f.Admissions.AdmitAsync(f.Aggregation, f.Bound.Position, f.Outcome); }
        await using var barrier = await f.Barrier("ProductionReferenceTimePublicationTransition", complete ? "UPDATE" : "INSERT");
        var p = await f.NewPublisher("C2", true);
        var reached = Signal(); var release = Signal();
        if (validatorFirst) p.AtStage = async (stage, _, _) => { if (stage == "references") { reached.TrySetResult(); await Reached(release); } };
        Task<string>? publish = validatorFirst ? p.Publish() : null;
        if (validatorFirst) await Reached(reached);
        var writer = complete ? f.Transitions.CompleteAsync(f.Aggregation, f.Bound.Position) : f.Begin();
        try
        {
            var writerSpid = await f.WaitFor(barrier.Spid, writer);
            if (validatorFirst) release.TrySetResult(); else publish = p.Publish();
            await f.WaitFor(writerSpid!.Value, publish!);
            await f.Capture(complete ? "C2: terminal update uncommitted; validator waits" : "C2: pending claim insert uncommitted; validator waits");
        }
        finally { release.TrySetResult(); await barrier.Release(); }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C3ActualAdmissionCannotConvertPendingAuthorityIntoMissingMetric(bool validatorFirst)
    {
        await using var f = await B1ConcurrencyPrototype.Create(output, false, quantities: 2);
        await f.Begin();
        await f.Admissions.AdmitAsync(f.Aggregation, f.Bound.Position, f.AllOutcomes[0]);
        Assert.Single(await f.Legacy.ReadAtRevisionAsync(f.Aggregation, new(1), CancellationToken.None));
        await f.Capture("C3: one of two required sources admitted; transition still pending");
        await using (var barrier = await f.Barrier("ProductionReferenceTimeOutcome", "INSERT"))
        {
            var p = await f.NewPublisher("C3-pending", true);
            var reached = Signal(); var release = Signal();
            if (validatorFirst) p.AtStage = async (stage, _, _) => { if (stage == "references") { reached.TrySetResult(); await Reached(release); } };
            Task<string>? attempt = validatorFirst ? p.Publish() : null;
            if (validatorFirst) await Reached(reached);
            var admission = f.Admissions.AdmitAsync(f.Aggregation, f.Bound.Position, f.AllOutcomes[1]);
            try
            {
                await f.WaitFor(barrier.Spid, admission);
                release.TrySetResult();
                attempt ??= p.Publish();
                var failure = await Assert.ThrowsAsync<InvalidDataException>(() => attempt);
                Assert.Equal("HistoricalEvidenceUnavailable", failure.Message);
                await f.Capture("C3: outcome inserted but admission uncommitted; pending authority rejected");
                AssertUnpublished(await f.State(p));
            }
            finally { release.TrySetResult(); await barrier.Release(); }
            await admission.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False((await f.Transitions.ReadAsync(f.Aggregation, f.Bound.Position))!.IsCompleted);
            var stillPending = await Assert.ThrowsAsync<InvalidDataException>(() => p.Publish());
            Assert.Equal("HistoricalEvidenceUnavailable", stillPending.Message);
        }
        await f.Transitions.CompleteAsync(f.Aggregation, f.Bound.Position);
        var completed = await f.NewPublisher("C3-completed", true);
        Assert.Equal("Published", await completed.Publish());
        Assert.Equal(f.Receipt(completed.Id.Value, true), await completed.Read());
        AssertPublished(await f.State(completed), 5m);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C6IndependentPublishersShareOneImmutableWitness(bool reversePublishers)
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var first = await f.NewPublisher("C6-first", true); var second = await f.NewPublisher("C6-second", true);
        if (reversePublishers) (first, second) = (second, first);
        var reached = Signal(); var release = Signal(); var holder = 0;
        first.AtStage = async (stage, c, _) => { if (stage == "witness") { holder = c.ServerProcessId; reached.TrySetResult(); await Reached(release); } };
        var one = first.Publish(); await Reached(reached); var two = second.Publish();
        try { await f.WaitFor(holder, two); await f.Capture("C6: independent processor gates; actual observed resource determines serialization"); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(30));
        AssertPublished(await f.State(first)); AssertPublished(await f.State(second));
    }

    [Fact]
    public async Task C6ArchiveKeyOrderIsIdenticalForOppositeValidWitnessEnumerations()
    {
        await using var f = await B1ConcurrencyPrototype.Create(output);
        var shiftWitness = f.Snapshot(f.Inputs).AvailabilityScopeConflictEvidence!;
        var dayKey = new OperationalMetricEvaluationKey(f.Machine,
            new OperationalMetricPeriodId.ProductionDay(new(f.Site, new(2026, 10, 16))),
            FactoryConnect.Core.Metrics.BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned);
        var dayWitness = f.Snapshot(f.Inputs, dayKey).AvailabilityScopeConflictEvidence!;
        Assert.NotEqual(Convert.ToHexString(B1ConcurrencyPrototype.WitnessBytes(shiftWitness)), Convert.ToHexString(B1ConcurrencyPrototype.WitnessBytes(dayWitness)));
        await using var first = await f.Open(); await using var second = await f.Open();
        await using var one = first.BeginTransaction(System.Data.IsolationLevel.Serializable);
        await using var two = second.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var reached = Signal(); var release = Signal();
        var left = f.WriteWitnessSet(first, one, [shiftWitness, dayWitness], async () => { reached.TrySetResult(); await Reached(release); });
        await Reached(reached);
        var right = f.WriteWitnessSet(second, two, [dayWitness, shiftWitness]);
        try { await f.WaitFor(first.ServerProcessId, right); await f.Capture("C6: archive-layer opposite valid shift/day witness enumeration; canonical byte-key order"); }
        finally { release.TrySetResult(); }
        await left.WaitAsync(TimeSpan.FromSeconds(30));
        await one.CommitAsync();
        await right.WaitAsync(TimeSpan.FromSeconds(30));
        await two.CommitAsync();
        await using var check = await f.Open();
        Assert.Equal(2, await B1ConcurrencyPrototype.Scalar(check, null, "SELECT COUNT(*) FROM dbo.OperationalMetricConflictWitnessArchive;"));
        var recorded = f.Events.Select(item => JsonSerializer.SerializeToElement(item))
            .Where(item => item.TryGetProperty("Stage", out var stage) && stage.GetString()!.StartsWith("archive-key:", StringComparison.Ordinal)).ToArray();
        var firstKeys = recorded.Where(item => item.GetProperty("Spid").GetInt32() == first.ServerProcessId).Select(item => item.GetProperty("Stage").GetString()!).ToArray();
        var secondKeys = recorded.Where(item => item.GetProperty("Spid").GetInt32() == second.ServerProcessId).Select(item => item.GetProperty("Stage").GetString()!).ToArray();
        Assert.Equal(2, firstKeys.Length);
        Assert.Equal(firstKeys.Order(StringComparer.Ordinal), firstKeys);
        Assert.Equal(firstKeys, secondKeys);

        // This ancillary archive-layer stress fixture intentionally has no result
        // publication. C6's separate full publisher tests establish result closure.
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
        var readerReached = Signal(); var readerRelease = Signal(); var readerSpid = 0;
        p.AtStage = async (stage, c, _) => { if (stage == "read-validated") { readerSpid = c.ServerProcessId; readerReached.TrySetResult(); await Reached(readerRelease); } };
        var heldRead = p.Read(); await Reached(readerReached);
        f.Events.Enqueue(new { Schedule = "C7 legacy replay and neighbor Begin start while historical validator holds A4/R1", Utc = DateTimeOffset.UtcNow });
        var replay = f.Legacy.PublishAsync(f.Aggregation, f.Bound.Position, f.Outcome, CancellationToken.None);
        var begin = f.Transitions.BeginAsync(f.Aggregation, next.Position, f.Bound.Position, new(1), 1);
        var sourceWriters = Task.WhenAll(replay, begin);
        try { await f.WaitFor(readerSpid, sourceWriters, true); await f.Capture("C7: actual legacy replay/Begin versus held reporting historical cut"); }
        finally { readerRelease.TrySetResult(); }
        await sourceWriters.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(f.Receipt(p.Id.Value, true), await heldRead.WaitAsync(TimeSpan.FromSeconds(30)));
        p.AtStage = null;
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
    [InlineData("prefix")]
    [InlineData("slot")]
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

    private static void AssertPublished(object[] state, decimal expectedPosition = 4m)
    { Assert.Equal(1, state[0]); Assert.Equal(1, state[1]); Assert.Equal(1, state[2]); Assert.Equal(1, state[3]); Assert.Equal(1, state[4]); Assert.Equal(2, state[5]); Assert.Equal(expectedPosition, state[6]); }
    private static void AssertUnpublished(object[] state)
    { Assert.Equal(0, state[0]); Assert.Equal(0, state[1]); Assert.Equal(0, state[2]); Assert.Equal(0, state[3]); Assert.Equal(0, state[4]); Assert.Equal(0, state[5]); Assert.Equal(1m, state[6]); }
}
