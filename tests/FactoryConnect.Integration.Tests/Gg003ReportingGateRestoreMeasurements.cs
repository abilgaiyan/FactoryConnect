using System.Data;
using System.Diagnostics;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003ReportingGateRestoreMeasurements(ITestOutputHelper output)
{
    private const string ConnectionVariable = "FACTORYCONNECT_GG003_GATE_RCA_CONNECTION_STRING";
    private const string ProcessorVariable = "FACTORYCONNECT_GG003_GATE_RCA_PROCESSOR";
    private const string DayVariable = "FACTORYCONNECT_GG003_GATE_RCA_DATE";

    [GateRestoreFact]
    public async Task ExactProcessorReplayAndReportingMeasureOfflineContention()
    {
        var settings = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        Assert.True(OperatingSystem.IsWindows());
        Assert.StartsWith("(localdb)\\", settings.DataSource, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("FactoryConnect_GG003_GATE_RCA_", settings.InitialCatalog, StringComparison.Ordinal);
        var processor = new OperationalMetricProjectionProcessorId(Environment.GetEnvironmentVariable(ProcessorVariable)!);
        var date = DateOnly.ParseExact(Environment.GetEnvironmentVariable(DayVariable)!, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        var machine = new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var token = deadline.Token;
        await using var inspect = new SqlConnection(settings.ConnectionString);
        await inspect.OpenAsync(token);
        await using (var guard = inspect.CreateCommand())
        {
            guard.CommandText = "SELECT DB_NAME();";
            Assert.Equal(settings.InitialCatalog, (string)(await guard.ExecuteScalarAsync(token))!);
        }
        short format;
        await using (var identity = inspect.CreateCommand())
        {
            identity.CommandText = "SELECT p.PublicationFormatVersion FROM dbo.OperationalMetricProjectionProcessor p JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId=p.MetricInputStreamRowId WHERE p.ProcessorKeyBinary=@Key AND p.ProcessorKey=@Name AND s.MachineId=@Machine;";
            identity.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(processor.Value);
            identity.Parameters.Add("@Name", SqlDbType.NVarChar, 256).Value = processor.Value;
            identity.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = machine.Value;
            format = Assert.IsType<short>(await identity.ExecuteScalarAsync(token));
            Assert.True(format is 1 or 2);
        }

        var application = "FC-Gate-Rca-" + Guid.NewGuid().ToString("N");
        using var trace = new ReportingGateCommandTrace(application);
        var readConnection = ReportingGateCommandTrace.Connection(settings.ConnectionString, application, "reader");
        var writeConnection = ReportingGateCommandTrace.Connection(settings.ConnectionString, application, "writer");
        var store = new SqlServerOperationalMetricProjectionStore(settings.ConnectionString);
        var checkpoint = await store.ReadCheckpointAsync(processor, MetricInputStreamId.ForMachine(machine), token);
        Assert.NotNull(checkpoint);
        output.WriteLine("processor=" + processor.Value);
        output.WriteLine("format=" + format);
        output.WriteLine("gate=" + SqlServerOperationalMetricProjectionProcessorGate.EncodeResource(processor));
        output.WriteLine("checkpoint=" + checkpoint.SourceRevision.Position.Value);
        output.WriteLine("requestedDate=" + date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        // No migrations, runtime advancement, restore, create or drop operations.
        // Replay the complete latest batch only; never derive a partial period batch.
        var payloads = new List<ReadOnlyMemory<byte>>();
        if (format == 2)
        {
            await using var archives = inspect.CreateCommand();
            archives.CommandText = "SELECT a.ResultPayload FROM dbo.OperationalMetricPublicationResultArchive a JOIN dbo.OperationalMetricProjectionProcessor p ON p.OperationalMetricProjectionProcessorRowId=a.OperationalMetricProjectionProcessorRowId WHERE p.ProcessorKeyBinary=@Key AND a.SourceRevisionPosition=@Position ORDER BY a.ResultId;";
            archives.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(processor.Value);
            archives.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", checkpoint.SourceRevision.Position.Value));
            await using var rows = await archives.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token)) payloads.Add((byte[])rows[0]);
            Assert.NotEmpty(payloads);
        }
        var legacy = new List<OperationalMetricProjection>();
        if (format == 1)
            foreach (var key in checkpoint.BatchManifest.ProjectionKeys)
            {
                var projection = await store.ReadProjectionAsync(processor, key, token);
                Assert.NotNull(projection);
                legacy.Add(projection);
            }

        // Fingerprints cover the full publication/archive snapshot before and after replay.
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(inspect, null);
        var baselineTimer = Stopwatch.StartNew();
        var baseline = await new SqlServerOperationalMetricProjectionSummaryReader(readConnection)
            .ReadCurrentPublicationSummariesAsync(processor, token);
        output.WriteLine("baselineWholePublicationReadMs=" + baselineTimer.Elapsed.TotalMilliseconds);
        Assert.NotEmpty(baseline);
        Assert.All(baseline, s => Assert.Equal(processor, s.ProcessorId));
        var requested = baseline.Where(s => s.Key.MachineId == machine &&
            s.Key.PeriodId is OperationalMetricPeriodId.ProductionDay day && day.ProductionDayId.BusinessDate == date &&
            s.Key.ContextKey == OperationalMetricEvaluationContextKey.Unpartitioned)
            .ToArray();
        Assert.NotEmpty(requested);
        Assert.All(requested, s => Assert.Equal(requested[0].SourceRevision, s.SourceRevision));
        output.WriteLine("retainedSummaryCount=" + baseline.Count);
        output.WriteLine("requestedDateSummaryCount=" + requested.Length);

        if (format == 2)
        {
            var batch = ContainmentArchiveBatch.Preflight(payloads);
            await using var c = new SqlConnection(ReportingGateCommandTrace.Connection(settings.ConnectionString, application, "verification"));
            await c.OpenAsync(token);
            await using var tx = c.BeginTransaction(IsolationLevel.Serializable);
            await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(c, tx, processor,
                OperationalMetricProjectionProcessorGateMode.Shared, token);
            var verifyTimer = Stopwatch.StartNew();
            var verified = await SqlContainmentVerifiedRead.TryReadAsync(c, tx, processor, token);
            output.WriteLine("standaloneWholePublicationVerificationMs=" + verifyTimer.Elapsed.TotalMilliseconds);
            Assert.NotNull(verified);
            var reconstructTimer = Stopwatch.StartNew();
            var reconstructed = await SqlContainmentHistoricalResultValidator.ReconstructVerifiedAsync(
                c, tx, batch.Results[0].Document.Key, batch.Cut, token);
            output.WriteLine("standaloneOneResultHistoricalReconstructionMs=" + reconstructTimer.Elapsed.TotalMilliseconds);
            Assert.Equal(batch.Results[0].Document.Key, reconstructed.Document.Key);
            await tx.CommitAsync(token);
        }
        else output.WriteLine("historicalVerification=not-applicable-to-format-1");

        var writerStarted = Stopwatch.GetTimestamp();
        var publication = Task.Run(async () =>
        {
            if (format == 2)
                await new SqlContainmentPublicationTransaction(writeConnection).PublishAsync(
                    payloads, checkpoint, token,
                    (_, _) => throw new InvalidOperationException("Exact replay unexpectedly performed DML."));
            else
                await new SqlServerOperationalMetricProjectionStore(writeConnection).CommitAsync(
                    new OperationalMetricProjectionCommit(processor, checkpoint, checkpoint, legacy), token);
            return Stopwatch.GetTimestamp();
        }, token);

        IReadOnlyList<OperationalMetricProjectionSummary>? concurrent = null;
        Exception? readFailure = null;
        var readerStarted = 0L;
        try
        {
            await trace.GateAsync("writer").WaitAsync(TimeSpan.FromSeconds(30), token);
            readerStarted = Stopwatch.GetTimestamp();
            concurrent = await new SqlServerOperationalMetricProjectionSummaryReader(readConnection)
                .ReadPeriodSummariesAsync(processor, machine, requested[0].Key.PeriodId,
                    OperationalMetricEvaluationContextKey.Unpartitioned, token);
        }
        catch (TimeoutException exception)
        {
            readFailure = exception;
            output.WriteLine("concurrentRead=processor-gate-timeout");
        }
        finally
        {
            // Observe the writer even if reporting fails.
            var returned = await publication.WaitAsync(token);
            output.WriteLine("publicationCallMs=" + Stopwatch.GetElapsedTime(writerStarted, returned).TotalMilliseconds);
            var writerGate = Assert.Single(trace.Entries, e => e.Role == "writer" && e.Kind == "gate");
            output.WriteLine("exclusiveGateAcquisitionMs=" + writerGate.Milliseconds);
            output.WriteLine("exclusiveGateToCallReturnUpperBoundMs=" +
                Stopwatch.GetElapsedTime(writerGate.CompletedAt, returned).TotalMilliseconds);
            output.WriteLine("readerCallStartedBeforeWriterReturned=" + (readerStarted < returned));
        }
        var after = await new SqlServerOperationalMetricProjectionSummaryReader(readConnection)
            .ReadCurrentPublicationSummariesAsync(processor, token);
        static string Identity(OperationalMetricProjectionSummary s) =>
            Convert.ToHexString(OperationalMetricEvaluationKeyV1Codec.Encode(s.Key)) + "|" +
            s.SourceRevision.Position.Value + "|" + s.Status + "|" +
            s.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + s.Unit + "|" +
            s.ReasonCode + "|" + s.ReasonOperandName;
        Assert.Equal(baseline.Select(Identity).Order(StringComparer.Ordinal), after.Select(Identity).Order(StringComparer.Ordinal));
        if (concurrent is not null)
            Assert.Equal(requested.Select(Identity).Order(StringComparer.Ordinal), concurrent.Select(Identity).Order(StringComparer.Ordinal));
        Assert.Equal(checkpoint, await store.ReadCheckpointAsync(processor, MetricInputStreamId.ForMachine(machine), token));
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(inspect, null));
        trace.Write(output);
        output.WriteLine("Read failure: " + (readFailure?.GetType().Name ?? "none"));
        output.WriteLine("Natural replay/read overlap is not guaranteed. Call overlap alone does not establish gate contention; inspect gate timings. Replay timings are not advancing-publication timings.");
    }

    public sealed class GateRestoreFactAttribute : FactAttribute
    {
        public GateRestoreFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || new[] { ConnectionVariable, ProcessorVariable, DayVariable }
                .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Requires explicit disposable GG003 LocalDB clone, exact processor and requested date.";
        }
    }
}
