using System.Diagnostics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class ReportingGateContentionBaselineTests(
    SqlContainmentAuthorityDatabaseFixture fixture, ITestOutputHelper output)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Fact]
    public async Task KnownProcessorPublicationReproducesDefaultSharedGateTimeoutThenCoherentRead()
    {
        var (envelopes, _) = await new SqlContainmentHistoricalResultIntegrationTests(fixture)
            .CreateArchiveTestEvidenceAsync();
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        var application = "FC-Gate-Rca-" + Guid.NewGuid().ToString("N");
        using var trace = new ReportingGateCommandTrace(application);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerConnection = ReportingGateCommandTrace.Connection(fixture.ConnectionString, application, "writer");
        var readerConnection = ReportingGateCommandTrace.Connection(fixture.ConnectionString, application, "reader");
        var publicationTimer = Stopwatch.StartNew();
        var pauseTimer = new Stopwatch();
        var publication = new SqlContainmentPublicationTransaction(writerConnection).PublishAsync(
            envelopes, null, deadline.Token, async (stage, token) =>
            {
                if (stage != ContainmentPublicationStage.ReferencesInserted) return;
                pauseTimer.Start();
                reached.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                finally { pauseTimer.Stop(); }
            });

        try
        {
            await reached.Task.WaitAsync(deadline.Token);
            output.WriteLine("processor=" + batch.Processor.Value);
            output.WriteLine("gate=" + SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(batch.Processor));
            output.WriteLine("gateTimeoutMs=" + SqlServerOperationalMetricProjectionProcessorGate.DefaultTimeoutMilliseconds);
            var readTimer = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
                await new SqlServerOperationalMetricProjectionSummaryReader(readerConnection)
                    .ReadCurrentPublicationSummariesAsync(batch.Processor, deadline.Token));
            output.WriteLine("blockedReadMs=" + readTimer.Elapsed.TotalMilliseconds);
            Assert.Equal("Processor gate acquisition timed out.", error.Message);
            Assert.False(publication.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await publication.WaitAsync(deadline.Token);
            publicationTimer.Stop();
        }

        var writerReturn = Stopwatch.GetTimestamp();
        var summaries = await new SqlServerOperationalMetricProjectionSummaryReader(readerConnection)
            .ReadCurrentPublicationSummariesAsync(batch.Processor, deadline.Token);
        Assert.Equal(batch.Results.Count, summaries.Count);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(batch.Processor, summary.ProcessorId);
            Assert.Equal(batch.Cut, summary.SourceRevision);
            Assert.Contains(batch.Results, result => result.Document.Key == summary.Key);
        });
        var writerGate = Assert.Single(trace.Entries, e => e.Role == "writer" && e.Kind == "gate");
        Assert.Contains(trace.Entries, e => e.Role == "reader" && e.Kind == "gate");
        output.WriteLine("publicationCallMs=" + publicationTimer.Elapsed.TotalMilliseconds);
        output.WriteLine("deliberatePauseMs=" + pauseTimer.Elapsed.TotalMilliseconds);
        output.WriteLine("exclusiveGateToCallReturnUpperBoundMs=" +
            Stopwatch.GetElapsedTime(writerGate.CompletedAt, writerReturn).TotalMilliseconds);

        // Exact replay must preserve the complete durable publication.
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null);
        await new SqlContainmentPublicationTransaction(writerConnection).PublishAsync(
            envelopes, null, deadline.Token, (_, _) => throw new InvalidOperationException("Replay performed DML."));
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, null));

        // An exclusive gate on the known processor must not block another exact identity.
        await using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, tx,
            batch.Processor, OperationalMetricProjectionProcessorGateMode.Exclusive, deadline.Token);
        var unrelated = await new SqlServerOperationalMetricProjectionSummaryReader(readerConnection)
            .ReadCurrentPublicationSummariesAsync(new("gate-unrelated-" + Guid.NewGuid().ToString("N")), deadline.Token);
        Assert.Empty(unrelated);
        await tx.RollbackAsync(deadline.Token);
        trace.Write(output);
    }
}
