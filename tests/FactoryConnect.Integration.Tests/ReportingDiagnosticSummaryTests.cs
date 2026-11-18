using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class ReportingDiagnosticSummaryTests
{
    [Fact]
    public void ExhaustedDetailDoesNotStopSummaryOrStarveSecondProcessor()
    {
        var summaries = new List<string>();
        using var request = new ReportingDiagnosticSession(summaries.Add, _ => { });
        ReportingDiagnosticSession.ReserveKnownProcessors(["GG", "MOXA"]);
        using (var attempt = ReportingDiagnosticSession.BeginAttempt("GG", 1))
        {
            for (var i = 0; i < 3000; i++)
            {
                using var row = OperationalMetricReportingDiagnostics.Start("containment.retained-row.verify");
                row.Tag("diagnostic.rows.total", 3000);
            }
            attempt!.EnumerationFinished();
        }
        using (ReportingDiagnosticSession.BeginAttempt("MOXA", 1))
            for (var i = 0; i < 256; i++)
            {
                using var row = OperationalMetricReportingDiagnostics.Start("containment.retained-row.verify");
            }
        Assert.Equal(2, summaries.Count);
        Assert.Contains("DetailAdmitted=2304;DetailSuppressed=696", summaries[0], StringComparison.Ordinal);
        Assert.Contains("rows.Started=3000,Completed=3000,Failed=0,Cancelled=0,Active=0", summaries[0], StringComparison.Ordinal);
        Assert.Contains("SummaryIntegrity=Complete", summaries[0], StringComparison.Ordinal);
        Assert.Contains("DetailAdmitted=256;DetailSuppressed=0", summaries[1], StringComparison.Ordinal);
        Assert.Contains("SqlCoverage=Unknown", summaries[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NinthProcessorIsSummaryOnly(bool known)
    {
        var summaries = new List<string>();
        using var request = new ReportingDiagnosticSession(summaries.Add, _ => { });
        if (known) ReportingDiagnosticSession.ReserveKnownProcessors(Enumerable.Range(0, 9).Select(i => $"P{i}"));
        for (var i = 0; i < 9; i++)
        {
            using var attempt = ReportingDiagnosticSession.BeginAttempt($"P{i}", 1);
            using var row = OperationalMetricReportingDiagnostics.Start("containment.retained-row.verify");
        }
        Assert.Equal(9, summaries.Count);
        Assert.Contains("DetailReservedAllowance=0;DetailAdmitted=0;DetailSuppressed=1", summaries[8], StringComparison.Ordinal);
        Assert.Contains("rows.Started=1,Completed=1", summaries[8], StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentAdmissionCannotOvershootCeiling()
    {
        using var request = new ReportingDiagnosticSession(_ => { }, _ => { });
        ReportingDiagnosticSession.ReserveKnownProcessors(Enumerable.Range(0, 8).Select(i => $"P{i}"));
        var admitted = 0;
        Parallel.For(0, 50000, i =>
        {
            if (request.Admit($"P{i % 8}")) Interlocked.Increment(ref admitted);
        });
        Assert.Equal(4096, admitted);
        Assert.False(request.Admit("P0"));
    }

    [Theory]
    [InlineData(true, "Cancelled")]
    [InlineData(false, "Failed")]
    public void OutcomeIsExclusiveAndDisposeIsIdempotent(bool cancel, string outcome)
    {
        var summaries = new List<string>();
        using var request = new ReportingDiagnosticSession(summaries.Add, _ => { });
        var attempt = ReportingDiagnosticSession.BeginAttempt("GG", 1)!;
        using (var row = OperationalMetricReportingDiagnostics.Start("containment.retained-row.verify"))
        {
            var error = cancel ? (Exception)new OperationCanceledException() : new InvalidDataException();
            row.Fail(error);
            attempt.Fail(error);
            row.Dispose();
        }
        attempt.Dispose(); attempt.Dispose();
        Assert.Single(summaries);
        Assert.Contains($"Outcome={outcome}", summaries[0], StringComparison.Ordinal);
        Assert.Contains(cancel ? "rows.Started=1,Completed=0,Failed=0,Cancelled=1,Active=0" :
            "rows.Started=1,Completed=0,Failed=1,Cancelled=0,Active=0", summaries[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NonterminalChildrenDegradeSummaryAndRetryGetsIndependentIdentity()
    {
        var summaries = new List<string>();
        using var request = new ReportingDiagnosticSession(summaries.Add, _ => { });
        var first = ReportingDiagnosticSession.BeginAttempt("GG", 1)!;
        var row = OperationalMetricReportingDiagnostics.Start("containment.retained-row.verify");
        first.Retried = true;
        first.Dispose();
        row.Dispose();
        using var second = ReportingDiagnosticSession.BeginAttempt("GG", 2);
        Assert.NotEqual(first.Id, second!.Id);
        Assert.Contains("SummaryIntegrity=Degraded", summaries[0], StringComparison.Ordinal);
        Assert.Contains("rows.Started=1,Completed=0,Failed=0,Cancelled=0,Active=1", summaries[0], StringComparison.Ordinal);
        Assert.Contains("rows.Started=0", second.Snapshot(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SinkFailuresDoNotEscapeAndHaveSeparateAcknowledgement(bool fallbackFails)
    {
        var acknowledgements = new List<string>();
        var fallbacks = 0;
        using var request = new ReportingDiagnosticSession(_ => throw new IOException("primary"), _ =>
        {
            fallbacks++;
            if (fallbackFails) throw new IOException("secondary");
        }, acknowledgements.Add);
        var attempt = ReportingDiagnosticSession.BeginAttempt("GG", 1)!;
        attempt.Dispose();
        Assert.Equal(1, fallbacks);
        Assert.Equal("Failed", attempt.SummaryEmission);
        Assert.Equal(fallbackFails ? "Failed" : "Succeeded", attempt.FallbackEmission);
        Assert.Contains("Delivery=Unverified", Assert.Single(acknowledgements), StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionFailureNeverClaimsCompleteAndAcceptedSummaryDoesNotClaimDurability()
    {
        var summaries = new List<string>();
        using var request = new ReportingDiagnosticSession(summaries.Add, _ => { });
        var attempt = ReportingDiagnosticSession.BeginAttempt("GG", 1)!;
        attempt.Degrade(); attempt.Dispose();
        Assert.Equal("Succeeded", attempt.SummaryEmission);
        Assert.Contains("SummaryIntegrity=Unknown", Assert.Single(summaries), StringComparison.Ordinal);
        Assert.Contains("SummaryDurability=Unknown", summaries[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NoSessionMeansNoSummaryCollector()
    {
        Assert.Null(ReportingDiagnosticSession.Current);
        Assert.Null(ReportingDiagnosticSession.BeginAttempt("GG", 1));
    }
    [Fact]
    public void NonProcessorDetailCannotConsumeReservationsAndRetryDoesNotResetAllowance()
    {
        var summaries = new List<string>();
        using var request = new ReportingDiagnosticSession(summaries.Add, _ => { });
        ReportingDiagnosticSession.ReserveKnownProcessors(["GG", "MOXA"]);
        for (var i = 0; i < 2048; i++) Assert.True(request.Admit(null));
        Assert.False(request.Admit(null));
        using (ReportingDiagnosticSession.BeginAttempt("GG", 1))
            for (var i = 0; i < 256; i++)
            {
                using var row = OperationalMetricReportingDiagnostics.Start("containment.retained-row.verify");
            }
        using (ReportingDiagnosticSession.BeginAttempt("GG", 2))
        {
            using var row = OperationalMetricReportingDiagnostics.Start("containment.retained-row.verify");
        }
        Assert.Contains("DetailAdmitted=0;DetailSuppressed=1", summaries[1], StringComparison.Ordinal);
        Assert.True(request.Admit("MOXA"));
    }

    [Fact]
    public void SummaryEmissionFailureCannotMaskOriginalVerificationException()
    {
        using var request = new ReportingDiagnosticSession(_ => throw new IOException(), _ => throw new IOException());
        var original = new InvalidDataException("authoritative");
        var observed = Assert.Throws<InvalidDataException>((Action)(() =>
        {
            using var attempt = ReportingDiagnosticSession.BeginAttempt("GG", 1);
            try { throw original; }
            catch (InvalidDataException error) { attempt!.Fail(error); throw; }
        }));
        Assert.Same(original, observed);
    }

}
