using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.HistoricalRecovery;
using Xunit;

namespace FactoryConnect.HistoricalRecovery.Tests;

public sealed class HistoricalRecoveryApplyTests
{
    internal static HistoricalRecoveryRunner CreateRunner(Writer? writer = null, Reporting? reporting = null,
        IRecoveryDeploymentVerifier? verifier = null)
    {
        var (_, capture) = HistoricalRecoveryPreviewTests.Fixture();
        return new(new HistoricalRecoveryPreviewTests.FakeReader(capture), reporting ?? new Reporting(), writer, verifier);
    }
    [Fact]
    public async Task PreviewNeverCallsRecoveryAndIsDeterministic()
    {
        var writer = new Writer(); var runner = CreateRunner(writer);
        var a = await runner.PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        var b = await runner.PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        Assert.Equal(a.Fingerprint, b.Fingerprint); Assert.Equal(0, writer.Calls);
        Assert.Equal("UNESTABLISHED", a.Reconstruction.DefinitionAuthority.ExactHistoricalExecutionSha);
        Assert.Equal(5, a.Projections.Count);
    }
    [Theory]
    [InlineData(OperationalMetricProjectionRecoveryOutcome.Recovered)]
    [InlineData(OperationalMetricProjectionRecoveryOutcome.Equivalent)]
    [InlineData(OperationalMetricProjectionRecoveryOutcome.Conflict)]
    public async Task ApplyPreservesStoreOutcomeAndAcceptsLiveMovement(OperationalMetricProjectionRecoveryOutcome outcome)
    {
        var reporting = new Reporting(); var writer = new Writer { Outcome = outcome, Reporting = reporting };
        var runner = CreateRunner(writer, reporting, new Verifier());
        var preview = await runner.PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        reporting.Position = 4000;
        var result = await runner.ApplyAsync(preview.Reconstruction.Target, RecoveryJson.Element(preview));
        Assert.Equal(outcome, result.Result.Outcome); Assert.Equal(1, writer.Calls);
        Assert.Equal(4001UL, result.AuthorityAfter.Checkpoint!.Position.Value);
    }
    [Fact]
    public async Task FingerprintMismatchRefusesMutation()
    {
        var writer = new Writer(); var runner = CreateRunner(writer, verifier: new Verifier());
        var preview = await runner.PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.ApplyAsync(preview.Reconstruction.Target,
            RecoveryJson.Element(preview with { Fingerprint = new string('0', 64) })));
        Assert.Equal(0, writer.Calls);
    }
    [Fact]
    public async Task ChangedReconstructionExpiresPreview()
    {
        var (_, capture) = HistoricalRecoveryPreviewTests.Fixture();
        var original = await CreateRunner().PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        var changed = capture with { PrefixMembership = capture.PrefixMembership.Select(m => m with { FactRowId = m.FactRowId + 10 }).ToArray() };
        var writer = new Writer(); var runner = new HistoricalRecoveryRunner(new HistoricalRecoveryPreviewTests.FakeReader(changed),
            new Reporting(), writer, new Verifier());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.ApplyAsync(original.Reconstruction.Target, RecoveryJson.Element(original)));
        Assert.Equal(0, writer.Calls);
    }
    [Fact]
    public async Task DeploymentFailureRefusesMutation()
    {
        var writer = new Writer(); var runner = CreateRunner(writer, verifier: new Verifier { Fail = true });
        var preview = await runner.PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(preview.Reconstruction.Target, RecoveryJson.Element(preview)));
        Assert.Equal(0, writer.Calls);
    }
    [Theory]
    [InlineData("Timeout")]
    [InlineData("Cancellation")]
    [InlineData("SQL")]
    public async Task OperationalFailureIsNotSemanticConflict(string failure)
    {
        var reporting = new Reporting(); var writer = new Writer { Reporting = reporting, Failure = failure };
        var runner = CreateRunner(writer, reporting, new Verifier());
        var preview = await runner.PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        var exception = await Record.ExceptionAsync(() => runner.ApplyAsync(preview.Reconstruction.Target, RecoveryJson.Element(preview)));
        Assert.NotNull(exception);
        Assert.Equal(failure switch { "Timeout" => typeof(TimeoutException), "Cancellation" => typeof(OperationCanceledException), _ => typeof(InvalidOperationException) }, exception.GetType());
    }
    [Fact]
    public async Task LatestManifestAndWrongBindingAreRejectedInPreview()
    {
        var preview = await CreateRunner().PreviewAsync(RecoveryTargets.ForDate("2026-10-04"));
        Assert.Equal(RecoveryClassification.BlockedByLatestManifest, HistoricalRecoveryRunner.Classify(preview.Reconstruction.Target,
            preview.Projections, preview.Authority with { Manifest = [preview.Projections[0].Key] }));
        Assert.Equal(RecoveryClassification.InvalidRevisionOrBinding, HistoricalRecoveryRunner.Classify(preview.Reconstruction.Target,
            preview.Projections, preview.Authority with { Checkpoint = null }));
        Assert.Throws<ArgumentException>(() => RecoveryTargets.ForDate("2026-10-06"));
    }
    [Fact]
    public void SourceContainsOnlyAuthorizedMutationSeam()
    {
        var source = string.Join("\n", Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "SafetySource"), "*.cs").Select(File.ReadAllText));
        foreach (var forbidden in new[] { ".CommitAsync(", ".PrepareAsync(", ".PublishAsync(", ".ConvergeAsync(", "Host.Create", "AddHostedService", "ExecuteNonQuery", "INSERT INTO", "UPDATE dbo.", "DELETE FROM", "MERGE INTO" })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, source.Split(".RecoverAsync(", StringSplitOptions.None).Length - 1);
        Assert.Contains("\"-ExecutionPolicy\", \"Bypass\", \"-File\"", source, StringComparison.Ordinal);
    }
    internal sealed class Reporting : IReportingAuthorityReader
    {
        public ulong Position { get; set; } = 3099;
        public IReadOnlyList<OperationalMetricProjection> Projections { get; set; } = [];
        public Task<ReportingAuthority> ReadReportingAuthorityAsync(RecoveryTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new ReportingAuthority(RecoveryTargets.Processor, new MetricAggregationCheckpoint(target.Revision.ProcessorId, target.Revision.StreamId, new MetricInputPosition(Position)), [], Projections));
    }
    internal sealed class Writer : IOperationalMetricProjectionRecoveryStore
    {
        public int Calls { get; private set; }
        public Reporting? Reporting { get; init; }
        public string? Failure { get; init; }
        public OperationalMetricProjectionRecoveryOutcome Outcome { get; init; } = OperationalMetricProjectionRecoveryOutcome.Recovered;
        public ValueTask<OperationalMetricProjectionRecoveryResult> RecoverAsync(OperationalMetricProjectionRecoveryRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure == "Timeout") throw new TimeoutException();
            if (Failure == "Cancellation") throw new OperationCanceledException();
            if (Failure == "SQL") throw new InvalidOperationException("Simulated infrastructure failure");
            if (Reporting is not null) { Reporting.Position++; if (Outcome != OperationalMetricProjectionRecoveryOutcome.Conflict) Reporting.Projections = request.Projections; }
            return ValueTask.FromResult(new OperationalMetricProjectionRecoveryResult(Outcome, Outcome == OperationalMetricProjectionRecoveryOutcome.Recovered ? 5 : 0));
        }
    }
    internal sealed class Verifier : IRecoveryDeploymentVerifier
    {
        public bool Fail { get; init; }
        public Task<JsonElement> VerifyAsync(CancellationToken cancellationToken) => Fail
            ? throw new InvalidOperationException("Unverified deployment") : Task.FromResult(RecoveryJson.Element(new { Status = "Verified" }));
    }
}
