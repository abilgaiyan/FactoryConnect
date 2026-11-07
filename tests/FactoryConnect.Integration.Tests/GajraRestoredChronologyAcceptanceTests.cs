using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

/// <summary>Opt-in, SELECT-only acceptance against the frozen local incident restore.</summary>
public sealed class GajraRestoredChronologyAcceptanceTests(ITestOutputHelper output)
{
    private const string Variable = "FACTORYCONNECT_P0_CHRONOLOGY_CONNECTION";

    [RestoredChronologyFact]
    public async Task ExactRestoredEvidenceReproducesInvalidProposalAndDeterministicHoldWithoutWrites()
    {
        var settings = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)!);
        Assert.Equal(@"DESKTOP-1PJK2OB\FCINVENTORY", settings.DataSource, ignoreCase: true);
        Assert.Equal("FactoryConnect_P0_20261105", settings.InitialCatalog, ignoreCase: false);
        var authority = new SqlServerMachineStateActivityAuthorityStore(settings.ConnectionString);
        var raw = new SqlServerObservationIngestionStore(settings.ConnectionString);
        var current = await authority.ReadAsync(GajraChronologyHoldTests.Processor, GajraChronologyHoldTests.Gg);
        Assert.NotNull(current);
        Assert.Equal(513501UL, current.Projection.Position.Value);
        Assert.Equal(4091UL, current.EvaluationAuthority.ProjectionRevision.Value);
        Assert.Equal(GajraChronologyHoldTests.Start, current.Projection.ActiveStartedAt);
        Assert.Equal(GajraChronologyHoldTests.Policy.Reference, current.EvaluationAuthority.AppliedContinuityPolicy);
        Assert.Equal(1793867708UL, current.EvaluationAuthority.LastConsumedInstanceId);
        var rawBoundary = await raw.ReadAsync(new(GajraChronologyHoldTests.Gg, new(513520), 1));
        var original = Assert.Single(rawBoundary.Observations);
        Assert.Equal(513521UL, original.Position.Value);
        Assert.Equal("STOPPED", original.Observation.Value);
        Assert.Equal(ObservationQuality.Good, original.Observation.Quality);
        Assert.Equal(GajraChronologyHoldTests.Earlier, original.Observation.Timestamp);
        var before = await CaptureAsync(settings.ConnectionString);
        var mappingBefore = await raw.ReadCheckpointAsync(GajraChronologyHoldTests.Mapping, GajraChronologyHoldTests.Gg);
        Assert.NotNull(mappingBefore);
        Assert.True(mappingBefore.Position.Value >= 513521);
        MachineStateActivityChronologyHold? previous = null;
        for (var restart = 0; restart < 2; restart++)
        {
            // Construct fresh replay state: no mapped input survives a restart.
            var reader = new RetainedActivityObservationReader(raw, raw, GajraChronologyHoldTests.Mapping,
                sink => GajraChronologyHoldTests.Mapper(GajraChronologyHoldTests.Gg, sink));
            var batch = await reader.ReadAsync(new(GajraChronologyHoldTests.Gg, current.Projection.Position, 100));
            var offending = Assert.Single(batch.Observations, item => item.Position.Value == 513521);
            Assert.Equal(1793867708UL, offending.InstanceId);
            Assert.Equal(103UL, offending.Sequence);
            Assert.Equal(GajraChronologyHoldTests.Earlier, offending.Observation.Timestamp);
            Assert.False(Assert.IsType<bool>(offending.Observation.Value));
            var legacy = Assert.IsType<MachineStateActivityContinuityPublication>(
                new GajraDeployedChronologyCalculator(GajraChronologyHoldTests.Processor)
                    .Calculate(current, batch.Observations, GajraChronologyHoldTests.Policy)).Publication;
            var invalid = Assert.Single(legacy.ActivityPeriods, item => item.Position.Value == 513521);
            Assert.True(invalid.Period.EndedAt < invalid.Period.StartedAt);
            output.WriteLine($"Deployed proposal: Position={invalid.Position.Value}; Start={invalid.Period.StartedAt:O}; End={invalid.Period.EndedAt:O}");
            // The proxy rejects every write; success proves the writer was never reached.
            var protectedStore = new GajraChronologyHoldTests.ReadOnlyAuthority(current);
            var runtime = new MappedObservationProcessingRuntime(reader,
                new JointMachineStateActivityCursorReader(protectedStore),
                new JointAuthorityMachineStateActivityProcessor(GajraChronologyHoldTests.Processor, protectedStore, GajraChronologyHoldTests.Policy),
                GajraChronologyHoldTests.Gg, new(100, TimeSpan.FromSeconds(1)));
            var cycle = await runtime.RunCycleWithOutcomeAsync();
            var hold = Assert.IsType<MachineStateActivityChronologyHold>(cycle.Hold);
            Assert.Equal(0, protectedStore.PublishCalls);
            if (previous is not null) Assert.Equal(previous, hold);
            previous = hold;
            output.WriteLine(hold.ToDiagnosticString());
        }
        Assert.Equal(before, await CaptureAsync(settings.ConnectionString));
        Assert.Equal(mappingBefore, await raw.ReadCheckpointAsync(GajraChronologyHoldTests.Mapping, GajraChronologyHoldTests.Gg));
        var after = await authority.ReadAsync(GajraChronologyHoldTests.Processor, GajraChronologyHoldTests.Gg);
        Assert.NotNull(after);
        Assert.Equal(current.Projection.Position, after.Projection.Position);
        Assert.Equal(current.EvaluationAuthority, after.EvaluationAuthority);
        output.WriteLine("PASS: exact evidence replay, negative legacy period, repeated hold, no writer calls, unchanged authority/signals/history/checkpoints.");
    }

    private static async Task<string> CaptureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT * FROM dbo.MachineStateActivityAuthority ORDER BY MachineId, StreamKeyBinary, StateProcessorIdOrderKey FOR JSON PATH) AS Authority,
                (SELECT * FROM dbo.MachineStateActivitySignal ORDER BY MachineId, StreamKeyBinary, StateProcessorIdOrderKey, SignalOrdinal FOR JSON PATH) AS Signals,
                (SELECT * FROM dbo.MachineStateChangeHistory ORDER BY MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision, OutputOrdinal FOR JSON PATH) AS StateHistory,
                (SELECT * FROM dbo.MachineActivityPeriodHistory ORDER BY MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision, OutputOrdinal FOR JSON PATH) AS ActivityHistory,
                (SELECT * FROM dbo.ObservationProcessingCheckpoint ORDER BY MachineId, StreamKeyBinary, ProcessorIdOrderKey FOR JSON PATH) AS Checkpoints
            FOR JSON PATH;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var result = new System.Text.StringBuilder();
        while (await reader.ReadAsync()) result.Append(reader.GetString(0));
        return result.ToString();
    }

    public sealed class RestoredChronologyFactAttribute : FactAttribute
    {
        public RestoredChronologyFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = "Requires explicitly selected local FactoryConnect_P0_20261105 incident restore.";
        }
    }
}
