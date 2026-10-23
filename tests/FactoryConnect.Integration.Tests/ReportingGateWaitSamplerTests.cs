using System.Text.Json;

namespace FactoryConnect.Integration.Tests;

public sealed class ReportingGateWaitSamplerTests
{
    [Fact]
    public async Task ConnectionFailureIsJournaledBeforeTheSameExceptionPropagates()
    {
        using var sampler = Create();
        var error = new InvalidOperationException("test-only failure");
        try
        {
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sampler.RunCoreAsync(_ => Task.FromException(error), _ => Task.CompletedTask, default));
            Assert.Same(error, caught);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sampler.Ready.Task);
            Assert.NotNull(sampler.FailureRecord);
            using var failure = JsonDocument.Parse(sampler.FailureRecord);
            Assert.Equal("connection-open", failure.RootElement.GetProperty("stage").GetString());
            Assert.Equal("initial-checkpoint-read", failure.RootElement.GetProperty("phase").GetString());
            Assert.Equal(0, failure.RootElement.GetProperty("successfulSamples").GetInt32());
            Assert.Equal(JsonValueKind.Null, failure.RootElement.GetProperty("sqlErrorNumber").ValueKind);
            Assert.Contains(sampler.FailureRecord, await File.ReadAllLinesAsync(sampler.JournalPath));
        }
        finally { File.Delete(sampler.JournalPath); }
    }

    [Fact]
    public async Task QueryFailureAfterReadinessRecordsCoverageAndRemainsFaulted()
    {
        using var sampler = Create();
        var calls = 0;
        var error = new TimeoutException("test-only query failure");
        try
        {
            var run = sampler.RunCoreAsync(_ => Task.CompletedTask,
                _ => ++calls == 1 ? Task.CompletedTask : Task.FromException(error), default);
            await sampler.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(error, await Assert.ThrowsAsync<TimeoutException>(() => run));
            Assert.NotNull(sampler.FailureRecord);
            using var failure = JsonDocument.Parse(sampler.FailureRecord);
            Assert.Equal("dmv-query", failure.RootElement.GetProperty("stage").GetString());
            Assert.Equal(1, failure.RootElement.GetProperty("successfulSamples").GetInt32());
            Assert.True(failure.RootElement.GetProperty("lastSuccessfulSampleAt").GetInt64() > 0);
            Assert.Contains(sampler.FailureRecord, await File.ReadAllLinesAsync(sampler.JournalPath));
            Assert.Contains(sampler.Events, line => line.Contains("sampler-query-start", StringComparison.Ordinal));
            Assert.Contains(sampler.Events, line => line.Contains("sampler-query-complete", StringComparison.Ordinal));
            Assert.True(run.IsFaulted);
        }
        finally { File.Delete(sampler.JournalPath); }
    }

    [Fact]
    public async Task CancellationStopsSamplingAndClosesJournalWithoutAFalseFailure()
    {
        using var sampler = Create();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var run = sampler.RunCoreAsync(_ => Task.CompletedTask, _ => Task.CompletedTask, cancellation.Token);
            await sampler.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(sampler.FailureRecord);
            Assert.Contains(sampler.Events, line => line.Contains("sampler-cancelled", StringComparison.Ordinal));
            Assert.Contains(sampler.Events, line => line.Contains("sampler-stop", StringComparison.Ordinal));
            // Exclusive access proves the writer has been closed before RunCoreAsync returns.
            using var journal = new FileStream(sampler.JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(journal.Length > 0);
        }
        finally { File.Delete(sampler.JournalPath); }
    }

    private static ReportingGateWaitSampler Create() => new("not-opened",
        "FC-Sampler-Unit-" + Guid.NewGuid().ToString("N"), () => "initial-checkpoint-read");
}
