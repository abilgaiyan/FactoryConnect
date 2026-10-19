using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003BoundedContainmentCatchupAcceptanceTests
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };
    private static readonly string[] SuccessfulBoundedOutcomes = ["TargetReached", "CycleLimitReached", "TimeBudgetReached"];
    private const string Variable = "FACTORYCONNECT_GG003_CATCHUP_CONNECTION_STRING";
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));

    [CatchupFact]
    public async Task ResumeThroughBoundedHistoricalCutAndRecordFirstFailure()
    {
        var settings = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)!);
        Assert.True(OperatingSystem.IsWindows());
        Assert.StartsWith("(localdb)\\", settings.DataSource, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("FactoryConnect_GG003_STAGEB_REPLAY_", settings.InitialCatalog, StringComparison.Ordinal);
        var report = Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_CATCHUP_REPORT")!;
        Assert.False(string.IsNullOrWhiteSpace(report));
        Assert.False(File.Exists(report));
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DB_NAME();";
            Assert.Equal(settings.InitialCatalog, (string)(await command.ExecuteScalarAsync())!);
        }
        // Deliberately no migration, restore, Edge host, source writer or legacy publisher here.
        var before = await Fingerprint(connection);
        await using (var legacy = connection.CreateCommand())
        {
            legacy.CommandText = "SELECT c.Position FROM dbo.OperationalMetricProjectionCheckpoint c JOIN dbo.OperationalMetricProjectionProcessor p ON p.OperationalMetricProjectionProcessorRowId=c.OperationalMetricProjectionProcessorRowId WHERE p.ProcessorKey=@Legacy;";
            legacy.Parameters.AddWithValue("@Legacy", $"operational-metrics:{Machine.Value}:builtins-v1");
            Assert.Equal(524m, (decimal)(await legacy.ExecuteScalarAsync())!);
        }
        var aggregation = new SqlServerMetricAggregationStore(settings.ConnectionString);
        var stream = MetricInputStreamId.ForMachine(Machine);
        var sourceProcessor = new MetricAggregationProcessorId($"metric-aggregation:{Machine.Value}");
        var processor = AvailabilityContainmentV1.ProjectionProcessorId(Machine);
        var store = new SqlServerOperationalMetricProjectionStore(settings.ConnectionString);
        var sourceHead = await aggregation.ReadCheckpointAsync(sourceProcessor, stream, default);
        Assert.NotNull(sourceHead);
        var start = await store.ReadCheckpointAsync(processor, stream, default);
        Assert.NotNull(start);
        Assert.True(start.SourceRevision.Position.Value >= 534);
        var target = ulong.Parse(Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_CATCHUP_TARGET") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (target == 0) target = sourceHead.Position.Value;
        Assert.InRange(target, start.SourceRevision.Position.Value, sourceHead.Position.Value);
        Assert.NotNull(await aggregation.ReadExactAsync(new(sourceProcessor, stream, new(target)), default));
        var limits = new ContainmentCatchupLimits(target,
            int.Parse(Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_CATCHUP_MAX_CYCLES") ?? "50", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromMinutes(int.Parse(Environment.GetEnvironmentVariable("FACTORYCONNECT_GG003_CATCHUP_MINUTES") ?? "30", System.Globalization.CultureInfo.InvariantCulture)));
        var reader = new CappedReader(aggregation, target);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var source = new CoherentOperationalMetricEvaluationBatchSource(catalog, reader, aggregation, sourceProcessor, stream,
            OperationalMetricEvaluationContextKey.Unpartitioned, useAvailabilityContainmentV1: true);
        var runtime = new OperationalMetricProjectionProcessingRuntime(processor, sourceProcessor, stream, source,
            new OperationalMetricProjectionFactory(catalog, processor), store,
            new CappedPrerequisite(reader, settings.ConnectionString), new SqlServerOperationalMetricContainmentPublisher(settings.ConnectionString));
        await File.WriteAllTextAsync(report + ".jsonl", JsonSerializer.Serialize(new { Phase = "Start", Database = settings.InitialCatalog,
            Start = start.SourceRevision.Position.Value, Target = target, CapturedSourceHead = sourceHead.Position.Value,
            limits.MaximumCycles, TimeBudgetSeconds = limits.TimeBudget.TotalSeconds, BeforeFingerprint = before }) + Environment.NewLine);
        var result = await BoundedContainmentCatchup.RunAsync(start.SourceRevision.Position.Value, limits,
            async (position, ct) => (await reader.ReadNextAsync(sourceProcessor, stream, new(sourceProcessor, stream, new(position)), ct))?.Revision.Position.Value,
            async ct => await runtime.RunCycleAsync(ct),
            async ct => (await store.ReadCheckpointAsync(processor, stream, ct))?.SourceRevision.Position.Value
                ?? throw new InvalidDataException("Containment checkpoint disappeared."),
            p => File.AppendAllTextAsync(report + ".jsonl", JsonSerializer.Serialize(p) + Environment.NewLine), default);
        var after = await Fingerprint(connection);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Result = result, SourceAndLegacyPreserved = before == after,
            CapturedSourceHead = sourceHead.Position.Value, FullCapturedCatchupComplete = result.TargetReached && target == sourceHead.Position.Value },
            ReportJsonOptions));
        Assert.Equal(before, after);
        Assert.Contains(result.Outcome, SuccessfulBoundedOutcomes);
        Assert.True(result.DurableReadbackSucceeded);
    }

    private static async Task<string> Fingerprint(SqlConnection connection)
    {
        var legacyAndInputs = await Gg003RestoredContainmentRuntimeAcceptanceTests.Fingerprint(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
            (SELECT * FROM dbo.MetricAggregationRevision ORDER BY MetricAggregationProcessorRowId,Position FOR JSON PATH,INCLUDE_NULL_VALUES) AS Revisions,
            (SELECT * FROM dbo.MetricAggregationRevisionShiftOccurrence ORDER BY MetricAggregationProcessorRowId,Position,ShiftOccurrenceIdentityHash FOR JSON PATH,INCLUDE_NULL_VALUES) AS Shifts,
            (SELECT * FROM dbo.MetricAggregationRevisionProductionDay ORDER BY MetricAggregationProcessorRowId,Position,SiteOrderKey,ProductionBusinessDate FOR JSON PATH,INCLUDE_NULL_VALUES) AS Days
            FOR JSON PATH;
            """;
        var ledger = (string)(await command.ExecuteScalarAsync())!;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyAndInputs + ledger)));
    }

    private sealed class CappedReader(IMetricAggregationRevisionReader reader, ulong target) : IMetricAggregationRevisionReader
    {
        public async ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(MetricAggregationProcessorId processorId, MetricInputStreamId streamId,
            MetricAggregationCheckpoint? afterRevision, CancellationToken cancellationToken)
        {
            var next = await reader.ReadNextAsync(processorId, streamId, afterRevision, cancellationToken);
            return next?.Revision.Position.Value > target ? null : next;
        }
        public ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(MetricAggregationCheckpoint revision, CancellationToken cancellationToken) =>
            revision.Position.Value > target ? ValueTask.FromResult<MetricAggregationRevisionChange?>(null) : reader.ReadExactAsync(revision, cancellationToken);
    }

    private sealed class CappedPrerequisite(CappedReader reader, string connectionString) : IOperationalMetricProjectionPrerequisite
    {
        public async ValueTask<MetricAggregationCheckpoint?> PrepareAsync(OperationalMetricEvaluationBatchRequest request, CancellationToken cancellationToken)
        {
            var next = await reader.ReadNextAsync(request.SourceProcessorId, request.SourceStreamId, request.KnownRevision, cancellationToken);
            if (next is null) throw new InvalidDataException("No next revision within the captured ceiling.");
            await new SqlServerProductionReferenceTimeConvergenceCoordinator(connectionString).ConvergeAsync(next.Revision,
                request.SourceStreamId.MachineId, request.KnownRevision?.Position, cancellationToken);
            var completion = await new SqlServerProductionReferenceTimePublicationTransitionStore(connectionString)
                .ReadAsync(next.Revision.ProcessorId, next.Revision.Position, cancellationToken);
            if (completion is not { IsCompleted: true, CompletedReferenceTimeRevision: not null })
                throw new InvalidDataException("Historical reference-time transition did not complete.");
            return next.Revision;
        }
    }

    public sealed class CatchupFactAttribute : FactAttribute
    {
        public CatchupFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = "Requires an explicit disposable Windows LocalDB catch-up clone.";
        }
    }
}
