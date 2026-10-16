using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerRunningProvenanceIntegrationTests
{
    [Theory]
    [InlineData(MachineState.Running)]
    [InlineData(MachineState.Stopped)]
    [InlineData(MachineState.Unknown)]
    public async Task ExactSourcesRetainObservedRunningZeroAndMissingWithoutClaimingCoverage(MachineState state)
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddAsync(0, 2, state); var revision = await f.AggregateAsync();
        var result = await f.Reader.ReadExactAsync(f.Request(revision), CancellationToken.None);
        if (state is MachineState.Unknown or MachineState.Offline)
        { Assert.Null(result.ObservedRunningDuration); Assert.Equal(RunningEvidenceState.Missing, result.State); Assert.Single(result.UnavailableIntervals); }
        else
        { Assert.Equal(state == MachineState.Running ? TimeSpan.FromHours(2) : TimeSpan.Zero, result.ObservedRunningDuration); Assert.Equal(RunningEvidenceState.Partial, result.State); }
        Assert.Single(result.Slices); Assert.Single(result.UnobservedIntervals);
        Assert.NotEmpty(result.Slices[0].ActivityHistoryFingerprint);
    }

    [Fact]
    public async Task HistoricalReplayAfterNewerRevisionAndRestartNeverUsesNewerFacts()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddAsync(0, 2, MachineState.Running); var first = await f.AggregateAsync();
        var original = await f.Reader.ReadExactAsync(f.Request(first), CancellationToken.None);
        await f.AddAsync(2, 4, MachineState.Stopped, 2); var second = await f.AggregateAsync();
        var restarted = new SqlServerRunningProvenanceReader(f.Database.ConnectionString);
        var replay = await restarted.ReadExactAsync(f.Request(first), CancellationToken.None);
        Assert.Equal(original.EvidenceFingerprint, replay.EvidenceFingerprint);
        Assert.Equal(original.ObservedRunningDuration, replay.ObservedRunningDuration);
        var newer = await restarted.ReadExactAsync(f.Request(second), CancellationToken.None);
        Assert.True(newer.HasInstanceBoundary); Assert.Equal(2, newer.Slices.Length);
        Assert.NotEqual(original.EvidenceFingerprint, newer.EvidenceFingerprint);
        Assert.Equal(RunningEvidenceState.Partial, newer.State);
    }

    [Fact]
    public async Task MissingRevisionWrongStreamAndWrongDayCannotBind()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddAsync(0, 2, MachineState.Running); var revision = await f.AggregateAsync();
        var request = f.Request(revision);
        Assert.Equal(RunningEvidenceReason.RevisionAbsent, (await f.Reader.ReadExactAsync(request with
            { Revision = new(revision.ProcessorId, revision.StreamId, new MetricInputPosition(revision.Position.Value + 1)) }, CancellationToken.None)).Reason);
        Assert.Equal(RunningEvidenceState.Incomparable, (await f.Reader.ReadExactAsync(request with
            { Revision = new(revision.ProcessorId, new MetricInputStreamId(revision.StreamId.MachineId, "wrong"), revision.Position) }, CancellationToken.None)).State);
        Assert.Equal(RunningEvidenceState.Incomparable, (await f.Reader.ReadExactAsync(request with
            { ProductionDay = new(f.Day.SiteId, f.Day.BusinessDate.AddDays(1)) }, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ExistingSourceOutputDoesNotPullStateFactFromBeyondCut()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddAsync(0, 2, MachineState.Running);
        var revision = await f.AggregateAsync(1);
        var atCut = await f.Reader.ReadExactAsync(f.Request(revision), CancellationToken.None);
        Assert.Null(atCut.ObservedRunningDuration); Assert.False(atCut.Slices[0].HasStateContribution);
        await f.AggregateAsync();
        Assert.Equal(atCut.EvidenceFingerprint, (await f.Reader.ReadExactAsync(f.Request(revision), CancellationToken.None)).EvidenceFingerprint);
    }

    [Theory]
    [InlineData("UPDATE dbo.ProductionTimeEligibilityOutput SET PayloadHash = 0x01;")]
    [InlineData("UPDATE dbo.ContextualizedActivityOutput SET PayloadHash = 0x01;")]
    [InlineData("UPDATE dbo.MetricInputFact SET MetricValue = N'999';")]
    [InlineData("UPDATE dbo.MachineActivityPeriodHistory SET InstanceId = InstanceId + 1;")]
    [InlineData("DELETE FROM dbo.ContextualizedActivityOutput;")]
    public async Task CorruptOrMissingSourceEvidenceFailsClosed(string mutation)
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddAsync(0, 2, MachineState.Running); var revision = await f.AggregateAsync();
        await using var connection = new SqlConnection(f.Database.ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(mutation, connection); await command.ExecuteNonQueryAsync();
        var result = await f.Reader.ReadExactAsync(f.Request(revision), CancellationToken.None);
        Assert.Equal(RunningEvidenceState.Corrupt, result.State); Assert.Null(result.ObservedRunningDuration);
    }

    [Fact]
    public async Task ConflictingOverlappingHistoriesCannotBeSummed()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddAsync(0, 3, MachineState.Running); await f.AddAsync(2, 4, MachineState.Stopped);
        var revision = await f.AggregateAsync();
        Assert.Equal(RunningEvidenceState.Corrupt, (await f.Reader.ReadExactAsync(f.Request(revision), CancellationToken.None)).State);
    }

    [Fact]
    public async Task MultiOutputPublicationResolvesEachDistinctPositionAndOutputIdentity()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddMultiOutputPublicationAsync(); var revision = await f.AggregateAsync();
        var result = await f.Reader.ReadExactAsync(f.Request(revision), CancellationToken.None);
        Assert.Equal(RunningEvidenceState.Partial, result.State);
        Assert.Equal(TimeSpan.FromHours(2), result.ObservedRunningDuration);
        Assert.Equal(2, result.Slices.Length);
        Assert.Equal(1UL, result.Slices[0].ObservationPosition);
        Assert.Equal(2UL, result.Slices[1].ObservationPosition);
        Assert.NotEqual(result.Slices[0].ActivityHistoryFingerprint, result.Slices[1].ActivityHistoryFingerprint);
        Assert.Equal(result.EvidenceFingerprint,
            (await new SqlServerRunningProvenanceReader(f.Database.ConnectionString).ReadExactAsync(f.Request(revision), CancellationToken.None)).EvidenceFingerprint);
    }

    [Fact]
    public async Task DuplicateHistoryPositionFromSqlTamperingRemainsCorruptRatherThanChoosingAnOrdinal()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddAsync(0, 2, MachineState.Running); var revision = await f.AggregateAsync();
        await using var connection = new SqlConnection(f.Database.ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT dbo.MachineActivityPeriodHistory
                (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision, OutputOrdinal,
                 Position, MachineState, StartedAt, EndedAt, InstanceId, Sequence)
            SELECT MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision, OutputOrdinal + 1,
                   Position, MachineState, StartedAt, EndedAt, InstanceId, Sequence
            FROM dbo.MachineActivityPeriodHistory;
            """, connection);
        await command.ExecuteNonQueryAsync();
        var result = await f.Reader.ReadExactAsync(f.Request(revision), CancellationToken.None);
        Assert.Equal(RunningEvidenceState.Corrupt, result.State); Assert.Null(result.ObservedRunningDuration);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
        public SqlStartupIsolatedDatabase Database { get; }
        private readonly MachineId _machine = MachineId.New();
        private readonly ObservationProcessorId _state = new("state-test");
        private readonly ObservationProcessorId _context = new("context-test");
        private readonly MetricAggregationProcessorId _aggregation = new("aggregation-test");
        private ObservationProcessingCheckpoint? _contextCheckpoint;
        private MetricAggregationCheckpoint? _revision;
        private ulong _position;
        private ObservationStreamId ObservationStream => new(_machine, "mtconnect-test");
        private MetricInputStreamId MetricStream => MetricInputStreamId.ForMachine(_machine);
        public ShiftOccurrenceId Shift { get; } = new(new SiteId("GG"), new ShiftScheduleAssignmentId("schedule"), new ShiftId("SHIFT-1"), Start, Start.AddHours(8));
        public ProductionDayId Day { get; } = new(new SiteId("GG"), new DateOnly(2026, 10, 16));
        public SqlServerRunningProvenanceReader Reader => new(Database.ConnectionString);
        private Fixture(SqlStartupIsolatedDatabase database) => Database = database;
        public static async Task<Fixture> CreateAsync()
        {
            var db = await SqlStartupIsolatedDatabase.CreateAsync();
            try
            {
                await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
                await new SqlServerMigrationEngine(SqlMigrationCatalog.Load(), new Clock()).ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
                var f = new Fixture(db);
                await using var command = new SqlCommand("""
                    INSERT dbo.ObservationStreamCheckpoint (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
                    VALUES (@Machine, @Stream, N'mtconnect-test', 1, 1);
                    """, connection);
                command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = f._machine.Value;
                command.Parameters.Add("@Stream", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(f.ObservationStream.StreamKey);
                await command.ExecuteNonQueryAsync(); return f;
            }
            catch { await db.DisposeAsync(); throw; }
        }
        public async Task AddAsync(int fromHour, int toHour, MachineState state, ulong instance = 1)
        {
            var previous = _position; _position++;
            var position = new ObservationPosition(_position); var start = Start.AddHours(fromHour); var end = Start.AddHours(toHour);
            var publication = new MachineStateActivityAuthorityPublication(previous == 0 ? null : new(previous),
                previous == 0 ? null : new StateProjectionAuthorityRevision(previous - 1),
                new(_state, ObservationStream, position, [], state, state, end), [],
                [new(_state, position, ObservationStream, instance, _position, new(_machine, state, start, end))],
                new(_state, ObservationStream, position, state, instance, new CurrentStatePolicyReference("continuity/default", "1.0")));
            Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
                await new SqlServerMachineStateActivityAuthorityStore(Database.ConnectionString).PublishAsync(publication));
            var activity = new ContextualizedActivityInterval
            {
                Id = new("activity-" + _position), SourceProcessorId = _state, SourcePosition = position, SourceStreamId = ObservationStream,
                SourceInstanceId = instance, SourceSequence = _position, CompanyId = new("company"), SiteId = Day.SiteId,
                MachineId = _machine, State = state, StartsAtUtc = start, EndsAtUtc = end, ShiftId = Shift.ShiftId, ShiftScheduleAssignmentId = Shift.ShiftScheduleAssignmentId,
            };
            await WriteContextAsync(activity);
        }
        public async Task AddMultiOutputPublicationAsync()
        {
            var first = new DurableMachineActivityPeriod(_state, new(1), ObservationStream, 1, 1,
                new(_machine, MachineState.Running, Start, Start.AddHours(2)));
            var second = new DurableMachineActivityPeriod(_state, new(2), ObservationStream, 1, 2,
                new(_machine, MachineState.Stopped, Start.AddHours(2), Start.AddHours(4)));
            var publication = new MachineStateActivityAuthorityPublication(null, null,
                new(_state, ObservationStream, new(2), [], MachineState.Stopped, MachineState.Stopped, Start.AddHours(4)), [], [first, second],
                new(_state, ObservationStream, new(2), MachineState.Stopped, 1, new CurrentStatePolicyReference("continuity/default", "1.0")));
            Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
                await new SqlServerMachineStateActivityAuthorityStore(Database.ConnectionString).PublishAsync(publication));
            _position = 2;
            foreach (var source in publication.ActivityPeriods)
            {
                await WriteContextAsync(new()
                {
                    Id = new("activity-" + source.Position.Value), SourceProcessorId = source.ProcessorId, SourcePosition = source.Position,
                    SourceStreamId = source.StreamId, SourceInstanceId = source.InstanceId, SourceSequence = source.Sequence,
                    CompanyId = new("company"), SiteId = Day.SiteId, MachineId = _machine, State = source.Period.State,
                    StartsAtUtc = source.Period.StartedAt, EndsAtUtc = source.Period.EndedAt,
                    ShiftId = Shift.ShiftId, ShiftScheduleAssignmentId = Shift.ShiftScheduleAssignmentId,
                });
            }
        }
        private async Task WriteContextAsync(ContextualizedActivityInterval activity)
        {
            var eligibility = ProductionTimeEligibilityAllocator.Allocate(activity, []);
            var facts = DurableMetricInputFactDeriver.Derive(eligibility, []).OrderBy(x => x.Key == MetricInputFactKeys.ScheduledDuration ? 0 : 1).ToArray();
            var next = new ObservationProcessingCheckpoint(_context, ObservationStream, activity.SourcePosition);
            await new SqlServerProductionContextProcessingStore(Database.ConnectionString).CommitAsync(new()
            {
                ExpectedCheckpoint = _contextCheckpoint, NextCheckpoint = next, ContextualizedActivity = [activity], EligibilityIntervals = eligibility,
                MetricFacts = facts, MetricInputs = facts.Select(x => new DurableMetricInputAppend(MetricStream, x, Shift, Day)).ToArray(),
            }, CancellationToken.None);
            _contextCheckpoint = next;
        }
        public async Task<MetricAggregationCheckpoint> AggregateAsync(int? count = null)
        {
            var batch = await new SqlServerMetricInputStore(Database.ConnectionString).ReadAsync(new(MetricStream, _revision?.Position, count ?? 100), CancellationToken.None);
            var revision = new MetricAggregationCheckpoint(_aggregation, MetricStream, batch.ThroughPosition!);
            await new SqlServerMetricAggregationStore(Database.ConnectionString).CommitAsync(new(_aggregation, _revision, revision, batch.Facts), CancellationToken.None);
            _revision = revision; return revision;
        }
        public RunningProvenanceRequest Request(MetricAggregationCheckpoint revision) => new(revision, Shift, Day, new(Start, Start.AddHours(8)));
        public ValueTask DisposeAsync() => Database.DisposeAsync();
        private sealed class Clock : ISqlMigrationUtcClock { public DateTimeOffset UtcNow => Start; }
    }
}
