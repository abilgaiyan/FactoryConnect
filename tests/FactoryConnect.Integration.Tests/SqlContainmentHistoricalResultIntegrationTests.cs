using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed partial class SqlContainmentHistoricalResultIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReconstructsAllOeeChildrenAndExactPerformanceBindingWithoutWrites(bool day, bool unavailable)
    {
        var seed = await SeedAsync("resolved", unavailable);
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await Snapshot(connection, transaction);
        var document = await Reconstruct(connection, transaction, seed, day);
        Assert.Equal(unavailable ? OperationalMetricEvaluationStatus.Unavailable : OperationalMetricEvaluationStatus.Calculated, document.Status);
        Assert.Equal(3, document.Dependencies.Count);
        Assert.Null(document.Witness);
        var performance = document.Dependencies[1].Result;
        Assert.Equal(0.4m, performance.Value);
        Assert.Equal(1, performance.Authority!.CompletedReferenceRevision);
        Assert.Equal([seed.SourceId.Value], performance.Authority.SourceQuantityIds);
        Assert.Equal([seed.StandardRevision], performance.Authority.StandardRevisions);
        Assert.Equal(4m, performance.Components[0].Value);
        Assert.Equal(1, performance.Components[0].InputCount);
        if (unavailable) Assert.NotNull(document.Dependencies[0].Result.Witness);
        foreach (var result in document.Dependencies.Select(d => d.Result).Append(document))
            _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction,
                OperationalMetricDependencySnapshotV2Codec.Encode(result), default);
        Assert.Equal(before, await Snapshot(connection, transaction));
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("missing-identity")]
    [InlineData("missing-standard")]
    [InlineData("ambiguous")]
    [InlineData("empty")]
    public async Task CompletedUnresolvedAuthorityIsVerifiedMissingReferenceTime(string mode)
    {
        var seed = await SeedAsync(mode);
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var document = await Reconstruct(connection, transaction, seed);
        var performance = document.Dependencies[1].Result;
        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, performance.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingReferenceTime, performance.ReasonCode);
        Assert.Equal(mode == "empty" ? 0 : 1, performance.Authority!.CompletedReferenceRevision);
        Assert.Equal(mode == "empty" ? 0 : 1, performance.Authority.SourceQuantityIds.Count);
        _ = await SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction,
            OperationalMetricDependencySnapshotV2Codec.Encode(document), default);
    }

    [Fact]
    public async Task LaterSourcesOutcomesAndStandardsDoNotRewriteEarlierAuthority()
    {
        var seed = await SeedAsync();
        byte[] original;
        await using (var connection = fixture.CreateConnection())
        {
            await connection.OpenAsync(); await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            original = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        }
        var standards = new SqlServerProductionStandardAuthority(fixture.ConnectionString);
        var latest = await standards.ReadCurrentCutAsync();
        await standards.PublishAsync(Standard(seed.Machine, seed.Company, seed.Shift.SiteId, latest.Revision + 1,
            seed.StandardId + "-later") with { EffectiveFromUtc = seed.Shift.StartsAtUtc.AddHours(1), SecondsPerUnit = 3m });
        var source = seed.Quantity!.Fact with { Id = new MetricInputFactId(Guid.NewGuid().ToString()),
            SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(Guid.NewGuid().ToString()),
            StartsAtUtc = seed.Shift.StartsAtUtc.AddHours(2), EndsAtUtc = seed.Shift.StartsAtUtc.AddHours(2) };
        var input = await new SqlServerMetricInputStore(fixture.ConnectionString).AppendAsync(
            new DurableMetricInputAppend(seed.Cut.StreamId, source, seed.Shift, seed.Day), default);
        var next = new MetricAggregationCheckpoint(seed.Cut.ProcessorId, seed.Cut.StreamId, input.Position);
        await new SqlServerMetricAggregationStore(fixture.ConnectionString).CommitAsync(new(seed.Cut.ProcessorId, seed.Cut, next, [input]), default);
        await new SqlServerProductionReferenceTimeConvergenceCoordinator(fixture.ConnectionString).ConvergeAsync(next, seed.Machine, seed.Cut.Position);
        await using var read = fixture.CreateConnection(); await read.OpenAsync();
        await using var stable = read.BeginTransaction(IsolationLevel.Serializable);
        var historical = await SqlContainmentHistoricalResultValidator.VerifyAsync(read, stable, original, default);
        Assert.Equal(1, historical.Dependencies[1].Result.Authority!.CompletedReferenceRevision);
        var current = await SqlContainmentHistoricalResultValidator.ReconstructAsync(read, stable,
            Key(seed, false, BuiltInOperationalMetricDefinitions.PerformanceId), next, default);
        Assert.Equal(2, current.Authority!.CompletedReferenceRevision);
        Assert.Equal(2, current.Authority.SourceQuantityIds.Count);
        Assert.Equal(2, current.Authority.StandardRevisions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOrPendingTransitionCannotCertifyMissingReferenceTimeEvenUnderUnavailableOee(bool pending)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var original = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        await Execute(connection, transaction, seed, pending
            ? "UPDATE dbo.ProductionReferenceTimePublicationTransition SET State=0, FinalProductionReferenceTimeRevision=NULL WHERE MetricAggregationProcessorRowId=@P;"
            : "DELETE FROM dbo.ProductionReferenceTimePublicationTransition WHERE MetricAggregationProcessorRowId=@P;");
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, original, default));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task MissingUnselectedHistoricalVersionCannotBeIgnored()
    {
        var unusedMachine = new MachineId(Guid.NewGuid());
        var unusedId = "unused-standard-" + unusedMachine.Value;
        var standards = new SqlServerProductionStandardAuthority(fixture.ConnectionString);
        var current = await standards.ReadCurrentCutAsync();
        await standards.PublishAsync(Standard(unusedMachine, new CompanyId("unused-company"), new SiteId("unused-site"),
            current.Revision + 1, unusedId));
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var original = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        await using var remove = connection.CreateCommand(); remove.Transaction = transaction;
        remove.CommandText = "DELETE FROM dbo.ProductionStandardVersion WHERE VersionId=@Id;";
        remove.Parameters.AddWithValue("@Id", unusedId);
        Assert.Equal(1, await remove.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, original, default));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("standard")]
    [InlineData("cut")]
    public async Task MissingHistoricalRowsAreAuthorityFailures(string missing)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var original = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        await Execute(connection, transaction, seed, missing switch
        {
            "outcome" => "DELETE FROM dbo.ProductionReferenceTimeOutcome WHERE MetricAggregationProcessorRowId=@P;",
            "standard" => "DELETE FROM dbo.ProductionStandardVersion WHERE VersionId=@Standard;",
            _ => "DELETE FROM dbo.ProductionReferenceTimePublicationCut WHERE MetricAggregationProcessorRowId=@P AND ProductionReferenceTimeRevision=1;"
        });
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, original, default));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("standard")]
    [InlineData("source")]
    [InlineData("transition-S")]
    public async Task ContradictoryCanonicalHistoryIsAnIntegrityFailure(string corruption)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var original = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        await Execute(connection, transaction, seed, corruption switch
        {
            "outcome" => "UPDATE dbo.ProductionReferenceTimeOutcome SET IdealProductionDurationSeconds=5 WHERE MetricAggregationProcessorRowId=@P;",
            "standard" => "UPDATE dbo.ProductionStandardVersion SET SecondsPerUnit=3 WHERE VersionId=@Standard;",
            "source" => "UPDATE dbo.MetricInputFact SET CompanyId=N'contradictory-company' WHERE SourceQuantityEvidenceId=@Source;",
            _ => "UPDATE dbo.ProductionReferenceTimePublicationTransition SET ProductionStandardAuthorityRevision=0 WHERE MetricAggregationProcessorRowId=@P;"
        });
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, original, default));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("R")]
    [InlineData("sources")]
    [InlineData("S")]
    [InlineData("quality-child")]
    public async Task CanonicalForgedBindingsAndLaterOeeChildrenAreRejected(string forgery)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var original = await Reconstruct(connection, transaction, seed);
        var dependencies = original.Dependencies.ToArray();
        if (forgery == "quality-child")
        {
            var quality = dependencies[2].Result;
            var first = quality.Components[0];
            var components = quality.Components.ToArray();
            components[0] = new(first.OperandName, first.SourceIdentity, first.SourceRevision, first.Dimension, 1m,
                first.Unit, first.InputCount, first.FirstInputTimestamp, first.LastInputTimestamp);
            dependencies[2] = dependencies[2] with { Result = Copy(quality, value: 0.5m, components: components) };
        }
        else
        {
            var performance = dependencies[1].Result;
            var authority = performance.Authority!;
            var changed = new ContainmentHistoricalAuthority(forgery == "R" ? authority.CompletedReferenceRevision + 1 : authority.CompletedReferenceRevision,
                forgery == "sources" ? ["forged-source"] : authority.SourceQuantityIds,
                forgery == "S" ? [authority.StandardRevisions[0] + 1] : authority.StandardRevisions);
            dependencies[1] = dependencies[1] with { Result = Copy(performance, authority: changed) };
        }
        var forged = Copy(original, dependencies: dependencies);
        var bytes = OperationalMetricDependencySnapshotV2Codec.Encode(forged);
        _ = OperationalMetricDependencySnapshotV2Codec.Decode(bytes);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, bytes, default));
    }

    [Fact]
    public async Task UnsupportedCodecAndMalformedEvidenceRemainDistinct()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var bytes = OperationalMetricDependencySnapshotV2Codec.Encode(await Reconstruct(connection, transaction, seed));
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, bytes.AsMemory(0, bytes.Length - 1), default));
        bytes[6] = 99;
        await Assert.ThrowsAsync<UnsupportedContainmentCodecException>(() =>
            SqlContainmentHistoricalResultValidator.VerifyAsync(connection, transaction, bytes, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedReadsHoldOutcomeAndStandardWritersUntilCallerRollback(bool standard)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var writer = fixture.CreateConnection(); await writer.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        _ = await Reconstruct(connection, transaction, seed);
        await using (var timeout = writer.CreateCommand()) { timeout.CommandText = "SET LOCK_TIMEOUT 500;"; await timeout.ExecuteNonQueryAsync(); }
        var sql = standard ? "UPDATE dbo.ProductionStandardVersion SET SourceReference=N'writer-change' WHERE VersionId=@Standard;"
            : "UPDATE dbo.ProductionReferenceTimeOutcome SET SelectedStandardSourceReference=N'writer-change' WHERE MetricAggregationProcessorRowId=@P;";
        var error = await Assert.ThrowsAsync<SqlException>(() => Execute(writer, null, seed, sql));
        Assert.Equal(1222, error.Number);
        await transaction.RollbackAsync();
        await Execute(writer, null, seed, sql);
    }

    internal async Task<(ReadOnlyMemory<byte>[] Envelopes, MetricAggregationCheckpoint Cut)> CreateArchiveTestEvidenceAsync()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var root = await Reconstruct(connection, transaction, seed);
        return ([OperationalMetricDependencySnapshotV2Codec.Encode(root.Dependencies[0].Result),
                 OperationalMetricDependencySnapshotV2Codec.Encode(root)], seed.Cut);
    }

    internal async Task<(ReadOnlyMemory<byte>[] First, ReadOnlyMemory<byte>[] Next)> CreatePublicationAdvanceEvidenceAsync()
    {
        var seed = await SeedAsync();
        ReadOnlyMemory<byte>[] first;
        await using (var c = fixture.CreateConnection())
        {
            await c.OpenAsync(); await using var tx = c.BeginTransaction(IsolationLevel.Serializable);
            var root = await Reconstruct(c, tx, seed);
            first = [OperationalMetricDependencySnapshotV2Codec.Encode(root.Dependencies[0].Result), OperationalMetricDependencySnapshotV2Codec.Encode(root)];
        }
        var source = seed.Quantity!.Fact with { Id = new MetricInputFactId(Guid.NewGuid().ToString()),
            SourceQuantityEvidenceId = new ProductionQuantityEvidenceId(Guid.NewGuid().ToString()),
            StartsAtUtc = seed.Shift.StartsAtUtc.AddHours(2), EndsAtUtc = seed.Shift.StartsAtUtc.AddHours(2) };
        var input = await new SqlServerMetricInputStore(fixture.ConnectionString).AppendAsync(
            new DurableMetricInputAppend(seed.Cut.StreamId, source, seed.Shift, seed.Day), default);
        var next = new MetricAggregationCheckpoint(seed.Cut.ProcessorId, seed.Cut.StreamId, input.Position);
        await new SqlServerMetricAggregationStore(fixture.ConnectionString).CommitAsync(new(seed.Cut.ProcessorId, seed.Cut, next, [input]), default);
        await new SqlServerProductionReferenceTimeConvergenceCoordinator(fixture.ConnectionString).ConvergeAsync(next, seed.Machine, seed.Cut.Position);
        await using var read = fixture.CreateConnection(); await read.OpenAsync(); await using var stable = read.BeginTransaction(IsolationLevel.Serializable);
        var result = await SqlContainmentHistoricalResultValidator.ReconstructAsync(read, stable,
            Key(seed, false, BuiltInOperationalMetricDefinitions.AvailabilityId), next, default);
        return (first, [OperationalMetricDependencySnapshotV2Codec.Encode(result)]);
    }

    internal async Task<ReadOnlyMemory<byte>[]> CreateVerifiedReadEvidenceAsync(bool day)
    {
        var seed = await SeedAsync();
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        var results = new List<ReadOnlyMemory<byte>>();
        foreach (var definition in BuiltInOperationalMetricDefinitions.All)
        {
            var document = await SqlContainmentHistoricalResultValidator.ReconstructAsync(c, tx, Key(seed, day, definition.Id), seed.Cut, default);
            results.Add(OperationalMetricDependencySnapshotV2Codec.Encode(document));
        }
        return results.ToArray();
    }

    private string TargetConnectionString => fixture.ConnectionString;
    private SqlConnection TargetConnection() => fixture.CreateConnection();

    private static ContainmentResultDocument Copy(ContainmentResultDocument d, decimal? value = null,
        ContainmentHistoricalAuthority? authority = null, IEnumerable<OperationalMetricComponentProjectionEvidence>? components = null,
        IEnumerable<ContainmentDependencyDocument>? dependencies = null) => new(d.ProcessorId, d.Key, d.Status, value ?? d.Value,
        d.Unit, d.ReasonCode, d.ReasonOperandName, d.SourceRevision, authority ?? d.Authority, d.Witness,
        components ?? d.Components, dependencies ?? d.Dependencies);

    private static OperationalMetricEvaluationKey Key(Seed seed, bool day, OperationalMetricDefinitionId definition) => new(seed.Machine,
        day ? new OperationalMetricPeriodId.ProductionDay(seed.Day) : new OperationalMetricPeriodId.Shift(seed.Shift), definition,
        OperationalMetricEvaluationContextKey.Unpartitioned);
    private static Task<ContainmentResultDocument> Reconstruct(SqlConnection connection, SqlTransaction transaction, Seed seed, bool day = false) =>
        SqlContainmentHistoricalResultValidator.ReconstructAsync(connection, transaction, Key(seed, day, BuiltInOperationalMetricDefinitions.OeeId), seed.Cut, default);

    private async Task<Seed> SeedAsync(string mode = "resolved", bool unavailable = true,
        string quantityShape = "good", bool converge = true, string utilization = "normal")
    {
        var machine = new MachineId(Guid.NewGuid());
        var company = new CompanyId("p3-rs-" + machine.Value);
        var site = new SiteId("p3-rs-site");
        var shift = new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId("p3-rs-schedule"), new ShiftId("p3-rs-shift"),
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 18));
        var standardId = "p3-rs-standard-" + machine.Value;
        var sourceId = new ProductionQuantityEvidenceId("p3-rs-source-" + machine.Value);
        var authority = new SqlServerProductionStandardAuthority(fixture.ConnectionString);
        var current = await authority.ReadCurrentCutAsync();
        if (mode is not ("missing-standard" or "empty"))
        {
            await authority.PublishAsync(Standard(machine, company, site, current.Revision + 1, standardId));
            if (mode == "ambiguous") await authority.PublishAsync(Standard(machine, company, site, current.Revision + 2, standardId + "-ambiguous"));
        }
        var stream = MetricInputStreamId.ForMachine(machine);
        var processor = new MetricAggregationProcessorId("p3-rs-" + machine.Value);
        var store = new SqlServerMetricInputStore(fixture.ConnectionString);
        var inputs = new List<PositionedMetricInputFact>();
        async Task<PositionedMetricInputFact> Append(string key, decimal value, bool planned, bool quantity)
        {
            var fact = new DurableMetricInputFact
            {
                Id = new MetricInputFactId(Guid.NewGuid().ToString()), Key = key, Value = value,
                Unit = quantity ? MetricInputFactUnits.Count : MetricInputFactUnits.Seconds,
                StartsAtUtc = shift.StartsAtUtc, EndsAtUtc = quantity ? shift.StartsAtUtc : shift.StartsAtUtc.AddSeconds((double)value),
                CompanyId = company, SiteId = site, MachineId = machine, ShiftId = shift.ShiftId, ShiftScheduleAssignmentId = shift.ShiftScheduleAssignmentId,
                PartId = mode == "missing-identity" ? null : new PartId("p3-rs-part"), OperationId = new OperationId("p3-rs-operation"),
                IsPlannedProductionTime = planned, PlannedProductionScheduleAssignmentId = planned ? new PlannedProductionScheduleAssignmentId("p3-rs-plan") : null,
                SourceEligibilityIntervalId = quantity ? null : new ProductionTimeEligibilityIntervalId(Guid.NewGuid().ToString()),
                SourceContextualizedActivityIntervalId = quantity ? null : new ContextualizedActivityIntervalId(Guid.NewGuid().ToString()),
                SourceQuantityEvidenceId = quantity ? sourceId : null
            };
            var input = await store.AppendAsync(new DurableMetricInputAppend(stream, fact, shift, day), default);
            inputs.Add(input); return input;
        }
        if (utilization != "missing-running") await Append(MetricInputFactKeys.RunningDuration, 10m, !unavailable, false);
        await Append(MetricInputFactKeys.PlannedProductionDuration, 20m, true, false);
        PositionedMetricInputFact? quantity = null;
        if (mode != "empty")
        {
            if (quantityShape is not ("orphan-good" or "orphan-rejected"))
                quantity = await Append(MetricInputFactKeys.PartCountIncrement, quantityShape == "zero" ? 0m : 2m, true, true);
            if (quantityShape is "good" or "all" or "zero" or "orphan-good")
                await Append(MetricInputFactKeys.GoodQuantity, quantityShape == "zero" ? 0m : quantityShape == "all" ? 1m : 2m, true, true);
            if (quantityShape is "rejected" or "all" or "orphan-rejected")
                await Append(MetricInputFactKeys.RejectedQuantity, 1m, true, true);
        }
        if (utilization != "missing-scheduled")
            await Append(MetricInputFactKeys.ScheduledDuration, utilization == "zero" ? 0m : 20m, true, false);
        var cut = new MetricAggregationCheckpoint(processor, stream, inputs[^1].Position);
        await new SqlServerMetricAggregationStore(fixture.ConnectionString).CommitAsync(new(processor, null, cut, inputs), default);
        var completed = converge ? await new SqlServerProductionReferenceTimeConvergenceCoordinator(fixture.ConnectionString).ConvergeAsync(cut, machine, null) : null;
        return new(machine, company, shift, day, cut, sourceId, standardId,
            completed?.ProductionStandardAuthorityRevision ?? (await authority.ReadCurrentCutAsync()).Revision, quantity);
    }

    private static ProductionStandardVersion Standard(MachineId machine, CompanyId company, SiteId site, long revision, string id) => new()
    {
        VersionId = id, CompanyId = company, SiteId = site, PartId = new PartId("p3-rs-part"), OperationId = new OperationId("p3-rs-operation"),
        MachineId = machine, SecondsPerUnit = 2m, EffectiveFromUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
        PublishedRevision = revision, SourceReference = "approved-p3-rs-standard"
    };

    private static async Task<int> Execute(SqlConnection connection, SqlTransaction? transaction, Seed seed, string sql)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = sql.Replace("@P", "(SELECT MetricAggregationProcessorRowId FROM dbo.MetricAggregationProcessor WHERE ProcessorKey=@Processor)", StringComparison.Ordinal);
        command.Parameters.AddWithValue("@Processor", seed.Cut.ProcessorId.Value);
        command.Parameters.AddWithValue("@Standard", seed.StandardId);
        command.Parameters.AddWithValue("@Source", seed.SourceId.Value);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Snapshot(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            DECLARE @Snapshot nvarchar(max) = (SELECT
              (SELECT * FROM dbo.MetricInputFact ORDER BY MetricInputFactRowId FOR JSON PATH, INCLUDE_NULL_VALUES) AS Facts,
              (SELECT * FROM dbo.ProductionReferenceTimePublicationTransition ORDER BY MetricAggregationProcessorRowId, TargetMetricAggregationPosition FOR JSON PATH) AS Transitions,
              (SELECT * FROM dbo.ProductionReferenceTimeOutcome ORDER BY MetricAggregationProcessorRowId, ProductionReferenceTimeRevision FOR JSON PATH) AS Outcomes,
              (SELECT * FROM dbo.ProductionReferenceTimePublicationCut ORDER BY MetricAggregationProcessorRowId, MetricAggregationPosition, ProductionReferenceTimeRevision FOR JSON PATH) AS Cuts,
              (SELECT * FROM dbo.ProductionStandardVersion ORDER BY VersionId FOR JSON PATH) AS Standards,
              (SELECT * FROM dbo.OperationalMetricProjection FOR JSON PATH) AS Projections,
              (SELECT * FROM dbo.OperationalMetricPublicationResultArchive FOR JSON PATH) AS Results,
              (SELECT * FROM dbo.OperationalMetricConflictWitnessArchive FOR JSON PATH) AS Witnesses
            FOR JSON PATH); SELECT @Snapshot;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Seed(MachineId Machine, CompanyId Company, ShiftOccurrenceId Shift, ProductionDayId Day,
        MetricAggregationCheckpoint Cut, ProductionQuantityEvidenceId SourceId, string StandardId, long StandardRevision,
        PositionedMetricInputFact? Quantity);
}
