using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class GajraChronologyHoldTests
{
    internal static readonly ObservationProcessorId Processor = new("machine-state-activity");
    internal static readonly ObservationProcessorId Mapping = new("canonical-mapping");
    internal static readonly ObservationStreamId Gg = new(
        new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f")), "mtconnect:DEWAS-SR-GG-003");
    internal static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-05T08:35:08.9193810Z", System.Globalization.CultureInfo.InvariantCulture);
    internal static readonly DateTimeOffset Earlier = DateTimeOffset.Parse("2026-11-05T08:26:00.4417150Z", System.Globalization.CultureInfo.InvariantCulture);
    internal static readonly CurrentStateContinuityPolicy Policy = CanonicalCurrentStateContinuityPolicies.Preserve;

    [Fact]
    public async Task DeployedCalculatorProposesNegativeUnknownPeriodAtExactCapturedBoundary()
    {
        var mapped = await NormalizeAsync(Gg, Raw(Gg, 513521, 103, "STOPPED", Earlier));
        Assert.False(Assert.IsType<bool>(mapped.Observation.Value));
        var proposal = Assert.IsType<MachineStateActivityContinuityPublication>(
            new GajraDeployedChronologyCalculator(Processor).Calculate(Snapshot(), [mapped], Policy)).Publication;
        var period = Assert.Single(proposal.ActivityPeriods);
        Assert.Equal(MachineState.Unknown, period.Period.State);
        Assert.Equal(Start, period.Period.StartedAt);
        Assert.Equal(Earlier, period.Period.EndedAt);
        Assert.True(period.Period.EndedAt < period.Period.StartedAt);
        Assert.Equal(513521UL, period.Position.Value);
        Assert.Equal(4091UL, proposal.ExpectedAuthorityRevision!.Value);
    }

    [Fact]
    public async Task HoldPreservesEvidenceAndNeverCallsAuthorityWriterAcrossRestart()
    {
        var observation = await NormalizeAsync(Gg, Raw(Gg, 513521, 103, "STOPPED", Earlier));
        var snapshot = Snapshot();
        var store = new ReadOnlyAuthority(snapshot);
        MachineStateActivityChronologyHold? first = null;
        for (var restart = 0; restart < 2; restart++)
        {
            var processor = new JointAuthorityMachineStateActivityProcessor(Processor, store, Policy);
            var hold = Assert.IsType<MachineStateActivityChronologyHold>(
                await processor.ProcessWithOutcomeAsync([observation]));
            Assert.Same(observation, hold.Observation);
            Assert.Equal(Start, hold.ActiveStartedAt);
            Assert.Equal(513501UL, hold.CommittedPosition!.Value);
            Assert.Equal(4091UL, hold.CommittedRevision!.Value);
            Assert.Equal(Policy.Reference, hold.AppliedPolicy);
            Assert.Contains("2026-11-05T08:26:00.4417150+00:00", hold.ToDiagnosticString(), StringComparison.Ordinal);
            if (first is not null) Assert.Equal(first, hold);
            first = hold;
        }
        Assert.Equal(0, store.PublishCalls);
        Assert.Same(snapshot, await store.ReadAsync(Processor, Gg));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstanceBoundaryUsesExistingResetOrPreserveSemantics(bool reset)
    {
        var observation = await NormalizeAsync(Gg, Raw(Gg, 513521, 103, "STOPPED", Earlier));
        observation = new(observation.Position, observation.StreamId, 1793867709, observation.Sequence, observation.Observation);
        var policy = reset ? new CurrentStateContinuityPolicy(new CurrentStatePolicyReference("continuity/reset", "1.0"), StateContinuityMode.Reset) : Policy;
        var result = new MachineStateActivityContinuityCalculator(Processor).Calculate(Snapshot(), [observation], policy);
        if (reset)
            Assert.Empty(Assert.IsType<MachineStateActivityContinuityPublication>(result).Publication.ActivityPeriods);
        else
            Assert.IsType<MachineStateActivityChronologyHold>(result);
    }

    [Fact]
    public async Task EqualTimestampStillPublishesAndSameStateRegressionDoesNotTriggerHold()
    {
        var mapped = await NormalizeAsync(Gg, Raw(Gg, 513521, 103, "STOPPED", Start));
        var calculator = new MachineStateActivityContinuityCalculator(Processor);
        var equal = Assert.IsType<MachineStateActivityContinuityPublication>(calculator.Calculate(Snapshot(), [mapped], Policy));
        var period = Assert.Single(equal.Publication.ActivityPeriods).Period;
        Assert.Equal(period.StartedAt, period.EndedAt);
        var unknown = new DurableMappedMachineObservation(mapped.Position, mapped.StreamId, mapped.InstanceId, mapped.Sequence,
            mapped.Observation with { Value = null, Quality = ObservationQuality.Uncertain, Timestamp = Earlier });
        Assert.IsType<MachineStateActivityContinuityPublication>(calculator.Calculate(Snapshot(), [unknown], Policy));
    }

    [Fact]
    public async Task ConflictAfterValidTransitionsRejectsWholeBatch()
    {
        var first = await NormalizeAsync(Gg, Raw(Gg, 513521, 103, "ACTIVE", Start.AddSeconds(1)));
        var second = await NormalizeAsync(Gg, Raw(Gg, 513522, 104, "STOPPED", Earlier));
        var store = new ReadOnlyAuthority(Snapshot());
        var result = await new JointAuthorityMachineStateActivityProcessor(Processor, store, Policy)
            .ProcessWithOutcomeAsync([first, second]);
        Assert.IsType<MachineStateActivityChronologyHold>(result);
        Assert.Equal(0, store.PublishCalls);
    }

    [Fact]
    public async Task PipelineHoldsOnlyActivityCursorWhileMappingAndMoxaAdvanceAndRestartRetainsConflict()
    {
        var moxa = new ObservationStreamId(MachineId.New(), "mtconnect:MOXA-001");
        var raw = new RawStore([
            Raw(Gg, 513521, 103, "STOPPED", Earlier),
            new DurableMachineObservation(new(1), moxa, 1793867708, 1, new MachineObservation
            { MachineId = moxa.MachineId, Source = "mtconnect", Address = "moxa001_status", Type = SignalType.Enumeration, Value = "RUNNING", Quality = ObservationQuality.Good, Timestamp = Start }),
        ]);
        var ggStore = new ReadOnlyAuthority(Snapshot());
        var moxaStore = new InMemoryMachineStateActivityAuthorityStore();
        for (var restart = 0; restart < 2; restart++)
        {
            // Fresh mapping sinks and replay processors model process memory loss.
            var ggPipeline = Pipeline(raw, Gg, ggStore);
            var moxaPipeline = Pipeline(raw, moxa, moxaStore);
            var set = new DurableObservationProcessingPipelineSet([ggPipeline, moxaPipeline], TimeSpan.FromMilliseconds(1));
            var notifications = 0;
            set.ChronologyHeld += _ => notifications++;
            await set.RunCycleAsync();
            Assert.NotNull(ggPipeline.ChronologyHold);
            Assert.Equal(1, notifications);
            Assert.Equal(513501UL, (await ggStore.ReadAsync(Processor, Gg))!.Projection.Position.Value);
            Assert.Equal(513521UL, (await raw.ReadCheckpointAsync(Mapping, Gg))!.Position.Value);
            Assert.Equal(1UL, (await raw.ReadCheckpointAsync(Mapping, moxa))!.Position.Value);
            Assert.Equal(1UL, (await moxaStore.ReadAsync(Processor, moxa))!.Projection.Position.Value);
            Assert.False(await set.RunCycleAsync());
            Assert.Equal(1, notifications); // No polling log flood; new process reports again.
        }
        Assert.Equal(0, ggStore.PublishCalls);
        Assert.Single(moxaStore.ReadStateChanges(Processor, moxa));
    }

    [Fact]
    public async Task OtherAuthorityFailuresStillEscapePipelineSet()
    {
        var raw = new RawStore([Raw(Gg, 513521, 103, "STOPPED", Start.AddSeconds(1))]);
        var store = new ReadOnlyAuthority(Snapshot());
        var set = new DurableObservationProcessingPipelineSet([Pipeline(raw, Gg, store)], TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => set.RunCycleAsync());
        Assert.Equal(1, store.PublishCalls);
    }

    [Fact]
    public async Task RetainedReplayCrossesUnmappedPagesAndStopsAtCanonicalCheckpoint()
    {
        var first = Raw(Gg, 513502, 79, "ACTIVE", Start);
        first = new(first.Position, first.StreamId, first.InstanceId, first.Sequence,
            first.Observation with { Address = "unmapped" });
        var conflict = Raw(Gg, 513521, 103, "STOPPED", Earlier);
        var notYetMapped = Raw(Gg, 513522, 104, "ACTIVE", Start.AddSeconds(1));
        var raw = new RawStore([first, conflict, notYetMapped]);
        var checkpoint = new ObservationProcessingCheckpoint(Mapping, Gg, new(513521));
        await raw.CommitAsync(new(null, checkpoint));
        var reader = new RetainedActivityObservationReader(raw, raw, Mapping, sink => Mapper(Gg, sink));
        var page = await reader.ReadAsync(new(Gg, new(513501), 1));
        Assert.Equal(513521UL, Assert.Single(page.Observations).Position.Value);
        Assert.False(page.HasMore);
        var end = await reader.ReadAsync(new(Gg, new(513521), 1));
        Assert.Empty(end.Observations);
        Assert.Equal(checkpoint, await raw.ReadCheckpointAsync(Mapping, Gg));
        Assert.Equal("STOPPED", conflict.Observation.Value);
        Assert.Equal(Earlier, conflict.Observation.Timestamp);
    }

    internal static MachineStateActivityAuthoritySnapshot Snapshot() => new(
        new MachineStateActivityProjection(Processor, Gg, new(513501),
            [new MachineSignalValue { Key = CanonicalSignalKeys.Running, Type = SignalType.Digital,
                Value = null, Quality = ObservationQuality.Uncertain, Source = "mtconnect", Timestamp = Start }],
            MachineState.Unknown, MachineState.Unknown, Start),
        new EvaluationAuthority(Processor, Gg, new(513501), MachineState.Unknown, 1793867708, Policy.Reference, new(4091)));

    internal static DurableMachineObservation Raw(ObservationStreamId stream, ulong position, ulong sequence, string value, DateTimeOffset timestamp) =>
        new(new(position), stream, 1793867708, sequence, new MachineObservation
        {
            MachineId = stream.MachineId, Source = "mtconnect", Address = "DEWAS-SR-GG-003_p1_ctl_exec",
            Type = SignalType.Enumeration, Value = value, Quality = ObservationQuality.Good, Timestamp = timestamp,
        });

    internal static IObservationProcessor Mapper(ObservationStreamId stream, IMappedMachineObservationSink sink) =>
        stream == Gg ? new ExecutionNormalizationProcessor(new MachineSignalMappingProcessor(Mapping,
            new MachineSignalMappingConfiguration { MachineId = stream.MachineId,
                Mappings = [new MachineSignalMappingDefinition { Source = "mtconnect", Address = "DEWAS-SR-GG-003_p1_ctl_exec",
                    SignalKey = CanonicalSignalKeys.Running, Type = SignalType.Digital }] },
            sink, new InMemoryMappingCoverageAuthorityStore()), stream, "mtconnect", "DEWAS-SR-GG-003_p1_ctl_exec") :
        new MoxaStatusNormalizationProcessor(new MachineSignalMappingProcessor(Mapping,
            new MachineSignalMappingConfiguration { MachineId = stream.MachineId,
                Mappings = [new MachineSignalMappingDefinition { Source = "mtconnect", Address = "moxa001_status",
                    SignalKey = CanonicalSignalKeys.OperationalState, Type = SignalType.Enumeration }] },
            sink, new InMemoryMappingCoverageAuthorityStore()), stream, "mtconnect", "moxa001_status");

    internal static async Task<DurableMappedMachineObservation> NormalizeAsync(ObservationStreamId stream, DurableMachineObservation observation)
    {
        var sink = new InMemoryMappedMachineObservationSink();
        await Mapper(stream, sink).ProcessAsync([observation]);
        return Assert.Single(sink.ReadObservations(stream));
    }

    private static DurableObservationProcessingPipeline Pipeline(RawStore raw, ObservationStreamId stream, IMachineStateActivityAuthorityStore store)
    {
        var options = new ObservationProcessingRuntimeOptions(100, TimeSpan.FromMilliseconds(1));
        var sink = new InMemoryMappedMachineObservationSink();
        return new(new(raw, raw, Mapper(stream, sink), stream, options),
            new(new RetainedActivityObservationReader(raw, raw, Mapping, replaySink => Mapper(stream, replaySink)),
                new JointMachineStateActivityCursorReader(store), new JointAuthorityMachineStateActivityProcessor(Processor, store, Policy), stream, options),
            options.PollingInterval);
    }

    internal sealed class ReadOnlyAuthority(MachineStateActivityAuthoritySnapshot snapshot) : IMachineStateActivityAuthorityStore
    {
        public int PublishCalls { get; private set; }
        public ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(ObservationProcessorId stateProcessorId, ObservationStreamId observationStreamId, CancellationToken cancellationToken = default) => ValueTask.FromResult<MachineStateActivityAuthoritySnapshot?>(snapshot);
        public ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(MachineStateActivityAuthorityPublication publication, CancellationToken cancellationToken = default)
        {
            PublishCalls++;
            throw new InvalidOperationException("Unexpected authority writer call.");
        }
    }

    private sealed class RawStore(DurableMachineObservation[] observations) : IDurableObservationReader, IObservationProcessingCheckpointStore
    {
        private readonly Dictionary<ObservationStreamId, ObservationProcessingCheckpoint> _checkpoints = [];
        public ValueTask<ObservationReadBatch> ReadAsync(ObservationReadRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new ObservationReadBatch(request.StreamId, observations.Where(item => item.StreamId == request.StreamId &&
                (request.AfterPosition is null || item.Position > request.AfterPosition)).Take(request.BatchSize).ToArray(), false));
        public ValueTask<ObservationProcessingCheckpoint?> ReadCheckpointAsync(ObservationProcessorId processorId, ObservationStreamId streamId, CancellationToken cancellationToken = default) => ValueTask.FromResult(_checkpoints.GetValueOrDefault(streamId));
        public ValueTask CommitAsync(ObservationProcessingCommit commit, CancellationToken cancellationToken = default)
        {
            _checkpoints[commit.Checkpoint.StreamId] = commit.Checkpoint;
            return ValueTask.CompletedTask;
        }
    }
}
