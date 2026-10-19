using System.Data;
using System.Diagnostics;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

public sealed partial class SqlContainmentHistoricalResultIntegrationTests
{
    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "normal")]
    [InlineData(false, "zero")]
    [InlineData(true, "zero")]
    [InlineData(false, "missing-running")]
    [InlineData(true, "missing-running")]
    [InlineData(false, "missing-scheduled")]
    [InlineData(true, "missing-scheduled")]
    public async Task UtilizationVerifiesHistoricalOperandsMissingEvidenceAndZeroDenominator(bool day, string shape)
    {
        // No R transition: Utilization must be independently verifiable using direct operands.
        var seed = await SeedAsync(converge: false, utilization: shape);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        var key = Key(seed, day, BuiltInOperationalMetricDefinitions.UtilizationId);
        var document = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction, key, seed.Cut, default);
        Assert.Null(document.Authority);
        Assert.Null(document.Witness);
        Assert.Empty(document.Dependencies);
        if (shape == "normal") { Assert.Equal(OperationalMetricEvaluationStatus.Calculated, document.Status); Assert.Equal(0.5m, document.Value); }
        else if (shape == "zero") { Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, document.Status); Assert.Equal(OperationalMetricEvaluationReasonCode.ZeroDenominator, document.ReasonCode); }
        else { Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, document.Status); Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, document.ReasonCode); }
        _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction,
            OperationalMetricDependencySnapshotV2Codec.Encode(document), default);
        Assert.Equal(before, await Snapshot(connection, transaction));
    }

    [Theory]
    [InlineData("produced-only")]
    [InlineData("good")]
    [InlineData("rejected")]
    [InlineData("all")]
    [InlineData("zero")]
    public async Task QuantitySiblingShapesProduceOneOutcomeAndIndependentlyVerifiedPerformanceAndQuality(string shape)
    {
        var seed = await SeedAsync(quantityShape: shape);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        var performance = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction,
            Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), seed.Cut, default);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, performance.Status);
        Assert.Equal(shape == "zero" ? 0m : 0.4m, performance.Value);
        Assert.Equal(1, performance.Authority!.CompletedReferenceRevision);
        Assert.Equal([seed.SourceId.Value], performance.Authority.SourceQuantityIds);
        Assert.Equal(1, performance.Components[0].InputCount); // Siblings are never counted as additional production.
        var quality = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction,
            Key(seed, false, BuiltInOperationalMetricDefinitions.QualityId), seed.Cut, default);
        if (shape is "produced-only" or "rejected")
            Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, quality.ReasonCode);
        else if (shape == "zero") Assert.Equal(OperationalMetricEvaluationReasonCode.ZeroDenominator, quality.ReasonCode);
        else Assert.Equal(shape == "all" ? 0.5m : 1m, quality.Value);
        foreach (var document in new[] { performance, quality })
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction,
                OperationalMetricDependencySnapshotV2Codec.Encode(document), default);
        Assert.Equal(before, await Snapshot(connection, transaction));
    }

    [Theory]
    [InlineData("orphan-good")]
    [InlineData("orphan-rejected")]
    public async Task GoodOrRejectedFactsAloneNeverEstablishProducedSourceAuthority(string shape)
    {
        var seed = await SeedAsync(quantityShape: shape);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var performance = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction,
            Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), seed.Cut, default);
        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, performance.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingReferenceTime, performance.ReasonCode);
        Assert.Equal(0, performance.Authority!.CompletedReferenceRevision);
        Assert.Empty(performance.Authority.SourceQuantityIds);
        Assert.Empty(performance.Authority.StandardRevisions);
        _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction,
            OperationalMetricDependencySnapshotV2Codec.Encode(performance), default);
    }

    [Theory]
    [InlineData("duplicate-produced")]
    [InlineData("fractional")]
    [InlineData("missing-source-id")]
    [InlineData("sibling-identity")]
    public async Task ConflictingQuantityShapesAreIntegrityFailures(string shape)
    {
        var seed = await SeedAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var sql = shape switch
        {
            "duplicate-produced" => "UPDATE dbo.MetricInputFact SET MetricInputKey=N'quantity.part-count-increment' WHERE SourceQuantityEvidenceId=@Source AND MetricInputKey=N'quantity.good';",
            "fractional" => "UPDATE dbo.MetricInputFact SET MetricValue=N'1.5' WHERE SourceQuantityEvidenceId=@Source AND MetricInputKey=N'quantity.part-count-increment';",
            "missing-source-id" => "UPDATE dbo.MetricInputFact SET SourceQuantityEvidenceId=NULL WHERE SourceQuantityEvidenceId=@Source AND MetricInputKey=N'quantity.part-count-increment';",
            _ => "UPDATE dbo.MetricInputFact SET PartId=N'contradictory-sibling-part' WHERE SourceQuantityEvidenceId=@Source AND MetricInputKey=N'quantity.good';"
        };
        // Use the authored key rather than assuming the wire spelling of GoodQuantity.
        sql = sql.Replace("quantity.good'", MetricInputFactKeys.GoodQuantity + "'", StringComparison.Ordinal);
        Assert.Equal(1, await Execute(connection, transaction, seed, sql));
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction,
                Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), seed.Cut, default));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task MultiShiftSourcesRetainPeriodMembershipAcrossCompletedRevisions()
    {
        var seed = await SeedAsync();
        var secondShift = new ShiftOccurrenceId(seed.Shift.SiteId, seed.Shift.ShiftScheduleAssignmentId, new ShiftId("p3-rs-shift-2"),
            seed.Shift.EndsAtUtc, seed.Shift.EndsAtUtc.AddHours(8));
        var source = seed.Quantity!.Fact with { Id = new MetricInputFactId(Guid.NewGuid().ToString()),
            SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(Guid.NewGuid().ToString()),
            ShiftId = secondShift.ShiftId, StartsAtUtc = secondShift.StartsAtUtc, EndsAtUtc = secondShift.StartsAtUtc, Value = 3m };
        var inputStore = new SqlServerMetricInputStore(TargetConnectionString);
        var produced = await inputStore.AppendAsync(new(seed.Cut.StreamId, source, secondShift, seed.Day), default);
        var running = await inputStore.AppendAsync(new(seed.Cut.StreamId, source with
        {
            Id = new MetricInputFactId(Guid.NewGuid().ToString()), Key = MetricInputFactKeys.RunningDuration, Value = 10m,
            Unit = MetricInputFactUnits.Seconds, EndsAtUtc = secondShift.StartsAtUtc.AddSeconds(10), SourceQuantityEvidenceId = null,
            SourceEligibilityIntervalId = new ProductionTimeEligibilityIntervalId(Guid.NewGuid().ToString()),
            SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId(Guid.NewGuid().ToString())
        }, secondShift, seed.Day), default);
        var next = new MetricAggregationCheckpoint(seed.Cut.ProcessorId, seed.Cut.StreamId, running.Position);
        await new SqlServerMetricAggregationStore(TargetConnectionString).CommitAsync(new(seed.Cut.ProcessorId, seed.Cut, next, [produced, running]), default);
        await new SqlServerProductionReferenceTimeConvergenceCoordinator(TargetConnectionString).ConvergeAsync(next, seed.Machine, seed.Cut.Position);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        foreach (var (period, expectedIds, expectedValue) in new[]
        {
            ((OperationalMetricPeriodId)new OperationalMetricPeriodId.Shift(seed.Shift), new[] { seed.SourceId.Value }, 0.4m),
            ((OperationalMetricPeriodId)new OperationalMetricPeriodId.Shift(secondShift), new[] { source.SourceQuantityEvidenceId!.Value.Value }, 0.6m),
            ((OperationalMetricPeriodId)new OperationalMetricPeriodId.ProductionDay(seed.Day), new[] { seed.SourceId.Value, source.SourceQuantityEvidenceId!.Value.Value }.Order(StringComparer.Ordinal).ToArray(), 0.5m)
        })
        {
            var key = new OperationalMetricEvaluationKey(seed.Machine, period, BuiltInOperationalMetricDefinitions.PerformanceId,
                OperationalMetricEvaluationContextKey.Unpartitioned);
            var document = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction, key, next, default);
            Assert.Equal(2, document.Authority!.CompletedReferenceRevision);
            Assert.Equal(expectedIds, document.Authority.SourceQuantityIds);
            Assert.Equal(expectedValue, document.Value);
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction,
                OperationalMetricDependencySnapshotV2Codec.Encode(document), default);
        }
    }

    [Fact]
    public async Task RealAdmissionRemainsInvisibleUntilCompletionCanPassTheReaderTransaction()
    {
        var seed = await SeedAsync(converge: false);
        var transitions = new SqlServerProductionReferenceTimePublicationTransitionStore(TargetConnectionString);
        await transitions.BeginAsync(seed.Cut.ProcessorId, seed.Cut.Position, null, ProductionReferenceTimeAuthorityRevision.Empty, seed.StandardRevision);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() => Reconstruct(connection, transaction, seed));
        var evidence = new ProductionQuantityEvidence
        {
            Id = seed.SourceId, CompanyId = seed.Company, SiteId = seed.Shift.SiteId, MachineId = seed.Machine,
            ShiftId = seed.Shift.ShiftId, PartId = seed.Quantity!.Fact.PartId, OperationId = seed.Quantity.Fact.OperationId,
            OccurredAtUtc = seed.Quantity.Fact.StartsAtUtc, PartCountIncrement = 2, GoodQuantity = 2
        };
        var standardCut = await new SqlServerProductionStandardAuthority(TargetConnectionString).ReadCutAsync(seed.StandardRevision);
        var resolution = ProductionStandardResolver.Resolve(evidence, seed.Shift, seed.Day, standardCut);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Real claim-bound admission commits while the reader has observed a pending transition.
        await new SqlServerCanonicalProductionReferenceTimePublisher(TargetConnectionString).PublishAsync(seed.Cut.ProcessorId,
            seed.Cut.Position, evidence, seed.Shift, seed.Day, standardCut,
            new(new ProductionReferenceTimeAuthorityRevision(1), resolution), cancellation.Token);
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() => Reconstruct(connection, transaction, seed));
        var completion = new SqlServerProductionReferenceTimeConvergenceCoordinator(TargetConnectionString)
            .ConvergeAsync(seed.Cut, seed.Machine, null, cancellation.Token);
        try
        {
            Assert.True(await ObserveWriterBoundary(connection, transaction, completion));
            await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() => Reconstruct(connection, transaction, seed));
        }
        finally { await transaction.RollbackAsync(); await completion.WaitAsync(TimeSpan.FromSeconds(15)); }
        await using var after = connection.BeginTransaction(IsolationLevel.Serializable);
        var document = await Reconstruct(connection, after, seed);
        Assert.Equal(0.4m, document.Dependencies[1].Result.Value);
        Assert.Equal(1, document.Dependencies[1].Result.Authority!.CompletedReferenceRevision);
    }

    [Fact]
    public async Task ActualAggregationWriterPreservesTheHistoricalCutAcrossConcurrentAdvancement()
    {
        var seed = await SeedAsync();
        var fact = seed.Quantity!.Fact with { Id = new MetricInputFactId(Guid.NewGuid().ToString()),
            SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(Guid.NewGuid().ToString()) };
        var input = await new SqlServerMetricInputStore(TargetConnectionString).AppendAsync(new(seed.Cut.StreamId, fact, seed.Shift, seed.Day), default);
        var next = new MetricAggregationCheckpoint(seed.Cut.ProcessorId, seed.Cut.StreamId, input.Position);
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var original = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var advancement = new SqlServerMetricAggregationStore(TargetConnectionString).CommitAsync(
            new(seed.Cut.ProcessorId, seed.Cut, next, [input]), cancellation.Token).AsTask();
        try
        {
            _ = await ObserveWriterBoundary(connection, transaction, advancement);
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, original, default);
        }
        finally { await transaction.RollbackAsync(); await advancement.WaitAsync(TimeSpan.FromSeconds(15)); }
        await using (var after = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, after, original, default);
            await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() => SqlContainmentHistoricalResultValidator.ReconstructAsync(
                connection, after, Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), next, default));
            await after.RollbackAsync();
        }
        await new SqlServerProductionReferenceTimeConvergenceCoordinator(TargetConnectionString).ConvergeAsync(next, seed.Machine, seed.Cut.Position);
        await using var completed = connection.BeginTransaction(IsolationLevel.Serializable);
        var current = await SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, completed,
            Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), next, default);
        Assert.Equal(2, current.Authority!.CompletedReferenceRevision);
        _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, completed, original, default);
    }

    [Fact]
    public async Task ActualStandardPublicationCannotChangeAFrozenHistoricalResolution()
    {
        var seed = await SeedAsync();
        var authority = new SqlServerProductionStandardAuthority(TargetConnectionString);
        var latest = await authority.ReadCurrentCutAsync();
        await using var connection = TargetConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var original = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var publication = authority.PublishAsync(Standard(seed.Machine, seed.Company, seed.Shift.SiteId, latest.Revision + 1,
            seed.StandardId + "-concurrent") with { SecondsPerUnit = 3m }, cancellation.Token);
        try
        {
            _ = await ObserveWriterBoundary(connection, transaction, publication);
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, original, default);
        }
        finally { await transaction.RollbackAsync(); await publication.WaitAsync(TimeSpan.FromSeconds(15)); }
        await using var after = connection.BeginTransaction(IsolationLevel.Serializable);
        var verified = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, after, original, default);
        Assert.Equal([seed.StandardRevision], verified.Dependencies[1].Result.Authority!.StandardRevisions);
    }

    private async Task<bool> ObserveWriterBoundary(SqlConnection readerConnection, SqlTransaction readerTransaction, Task writer)
    {
        await using var session = readerConnection.CreateCommand(); session.Transaction = readerTransaction;
        session.CommandText = "SELECT @@SPID;";
        var readerSession = (short)(await session.ExecuteScalarAsync())!;
        await using var monitor = TargetConnection(); await monitor.OpenAsync();
        await using var command = monitor.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@Reader AND database_id=DB_ID() AND wait_type LIKE N'LCK_M_%';";
        command.Parameters.AddWithValue("@Reader", readerSession);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            if ((int)(await command.ExecuteScalarAsync())! > 0) return true;
            if (writer.IsCompleted) { await writer; return false; }
            await Task.Delay(25);
        }
        Assert.Fail("No real writer commit or lock wait was observed within the bounded interleaving deadline.");
        return false;
    }
}
