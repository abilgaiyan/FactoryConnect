using System.Data;
using System.Diagnostics;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentHistoricalAuthorityIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Fact]
    public async Task CompleteFactsExcludeUnincorporatedAndLaterSourcesAndVerifyShiftAndDay()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await SnapshotAsync(connection, transaction);
        var inputs = await SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, seed.Cut, default);
        Assert.Equal(seed.Inputs.Take(3), inputs);
        foreach (var period in new OperationalMetricPeriodId[] { new OperationalMetricPeriodId.Shift(seed.Shift), new OperationalMetricPeriodId.ProductionDay(seed.Day) })
        {
            var snapshot = await Reconstruct(connection, transaction, seed, period);
            Assert.Equal(2, snapshot.Components.Count);
            var running = snapshot.Components.Single(c => c.SourceIdentity.ComponentKey == MetricInputFactKeys.RunningDuration).Aggregate;
            Assert.Equal(2m, running.Value);
            Assert.Equal(2, running.InputCount);
            Assert.Equal(seed.Inputs[0].Fact.StartsAtUtc, running.FirstInputTimestamp);
            Assert.Equal(seed.Inputs[2].Fact.EndsAtUtc, running.LastInputTimestamp);
            var planned = snapshot.Components.Single(c => c.SourceIdentity.ComponentKey == MetricInputFactKeys.PlannedProductionDuration).Aggregate;
            Assert.Equal(1m, planned.Value);
            Assert.Equal(1, planned.InputCount);
            var witness = Assert.IsType<AvailabilityScopeConflictEvidenceV1>(snapshot.AvailabilityScopeConflictEvidence);
            Assert.Equal(seed.Inputs[0], witness.Contribution);
            Assert.Equal(witness, await SqlContainmentAvailabilityAuthorityValidator.VerifyWitnessAsync(connection, transaction,
                ContainmentWitnessDocument.FromEvidence(witness), default));
        }
        Assert.Equal(before, await SnapshotAsync(connection, transaction));
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task OrderedHistoricalAccessPreservesCompleteFactsAtExactCut(int throughPosition)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var cut = new MetricAggregationCheckpoint(seed.Cut.ProcessorId, seed.Cut.StreamId,
            new MetricInputPosition((ulong)throughPosition));
        // Position 4 is unincorporated; position 5 belongs only to the later revision.
        var expected = seed.Inputs.Where(input => input.Position.Value <= (ulong)throughPosition &&
            input.Position.Value != 4).ToArray();
        using var parent = new Activity("historical-timing-test").Start();
        var stages = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == parent.TraceId) stages.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var actual = await SqlContainmentHistoricalContributionReader.ReadValidatedAsync(
            connection, transaction, cut, default);
        Assert.Equal(expected, actual.Inputs);
        foreach (var stage in new[] { "historical.exact-cut.resolve", "historical.sql.execute",
            "historical.rows.read-materialize-validate", "historical.aggregate", "historical.read.validate" })
        {
            var span = Assert.Single(stages, activity => activity.OperationName == stage);
            Assert.Equal(true, span.GetTagItem("stage.completed"));
            Assert.True(span.Duration >= TimeSpan.Zero);
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task MissingRevisionAndProcessorDifferFromContradictoryStream()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction,
                new(seed.Cut.ProcessorId, seed.Cut.StreamId, new MetricInputPosition(2)), default));
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction,
                new(new MetricAggregationProcessorId("absent-" + Guid.NewGuid()), seed.Cut.StreamId, seed.Cut.Position), default));
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction,
                new(seed.Cut.ProcessorId, new MetricInputStreamId(seed.Machine, "wrong-stream"), seed.Cut.Position), default));
    }

    [Fact]
    public async Task MalformedPersistedValuesAreIntegrityFailuresAndCancellationPropagates()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, seed.Cut, cancellation.Token));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE dbo.MetricInputFact SET MetricValue = N'not-a-decimal' WHERE FactId = @Id;";
        command.Parameters.AddWithValue("@Id", seed.Inputs[0].Fact.Id.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, seed.Cut, default));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task RejectsUnstableTransactions(IsolationLevel isolation)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(isolation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, seed.Cut, default));
    }

    [Fact]
    public async Task RejectsForeignAndCompletedTransactions()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await using var other = fixture.CreateConnection();
        await connection.OpenAsync();
        await other.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(other, transaction, seed.Cut, default));
        await transaction.RollbackAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, seed.Cut, default));
    }

    [Theory]
    [InlineData("fact")]
    [InlineData("aggregate")]
    [InlineData("selection")]
    public async Task RejectsCanonicalForgedWitnesses(string forgery)
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var snapshot = await Reconstruct(connection, transaction, seed, new OperationalMetricPeriodId.Shift(seed.Shift));
        var claim = ContainmentWitnessDocument.FromEvidence(snapshot.AvailabilityScopeConflictEvidence!);
        claim = forgery switch
        {
            "fact" => claim with { Contribution = new PositionedMetricInputFact(claim.Contribution.StreamId, claim.Contribution.Position,
                claim.Contribution.Fact with { CompanyId = new CompanyId("forged-company") }, claim.Contribution.ShiftOccurrenceId, claim.Contribution.ProductionDayId) },
            "aggregate" => claim with { Numerator = new OperationalMetricComponentProjectionEvidence(claim.Numerator.OperandName, claim.Numerator.SourceIdentity,
                claim.Numerator.SourceRevision, claim.Numerator.Dimension, 3m, claim.Numerator.Unit, claim.Numerator.InputCount,
                claim.Numerator.FirstInputTimestamp, claim.Numerator.LastInputTimestamp) },
            _ => claim with { Contribution = seed.Inputs[2] }
        };
        // Successful round-trip proves the forgery is structurally canonical.
        claim = OperationalMetricConflictWitnessV1Codec.Decode(OperationalMetricConflictWitnessV1Codec.Encode(claim));
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
            SqlContainmentAvailabilityAuthorityValidator.VerifyWitnessAsync(connection, transaction, claim, default));
    }

    [Fact]
    public async Task ReadsCallerUncommittedChangesAndRollbackRestoresOriginalAuthority()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            var snapshot = await Reconstruct(connection, transaction, seed, new OperationalMetricPeriodId.Shift(seed.Shift));
            var claim = ContainmentWitnessDocument.FromEvidence(snapshot.AvailabilityScopeConflictEvidence!);
            await ChangeCompanyAsync(connection, transaction, seed.Inputs[0].Fact.Id.Value);
            var inputs = await SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, seed.Cut, default);
            Assert.Equal(new CompanyId("changed-company"), inputs[0].Fact.CompanyId);
            await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() =>
                SqlContainmentAvailabilityAuthorityValidator.VerifyWitnessAsync(connection, transaction, claim, default));
            await transaction.RollbackAsync();
        }
        await using var after = connection.BeginTransaction(IsolationLevel.Serializable);
        Assert.Equal(seed.Inputs.Take(3), await SqlContainmentHistoricalContributionReader.ReadAsync(connection, after, seed.Cut, default));
    }

    [Fact]
    public async Task SerializableReadHoldsSourceWriterUntilCallerRollback()
    {
        var seed = await SeedAsync();
        await using var connection = fixture.CreateConnection();
        await using var writer = fixture.CreateConnection();
        await connection.OpenAsync();
        await writer.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        _ = await SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, seed.Cut, default);
        await using (var timeout = writer.CreateCommand())
        {
            timeout.CommandText = "SET LOCK_TIMEOUT 500;";
            await timeout.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<SqlException>(() => ChangeCompanyAsync(writer, null, seed.Inputs[0].Fact.Id.Value));
        Assert.Equal(1222, error.Number);
        await transaction.RollbackAsync();
        await ChangeCompanyAsync(writer, null, seed.Inputs[0].Fact.Id.Value);
    }

    private static async Task ChangeCompanyAsync(SqlConnection connection, SqlTransaction? transaction, string factId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE dbo.MetricInputFact SET CompanyId = N'changed-company' WHERE FactId = @Id;";
        command.Parameters.AddWithValue("@Id", factId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static Task<OperationalMetricComponentSnapshot> Reconstruct(SqlConnection connection, SqlTransaction transaction,
        Seed seed, OperationalMetricPeriodId period) => SqlContainmentAvailabilityAuthorityValidator.ReconstructAsync(
            connection, transaction, seed.Cut, new(seed.Machine, period, BuiltInOperationalMetricDefinitions.AvailabilityId,
                OperationalMetricEvaluationContextKey.Unpartitioned), default);

    private async Task<Seed> SeedAsync()
    {
        var machine = new MachineId(Guid.NewGuid());
        var stream = MetricInputStreamId.ForMachine(machine);
        var processor = new MetricAggregationProcessorId("p3-" + machine.Value);
        var site = new SiteId("p3-site");
        var shift = new ShiftOccurrenceId(site, new ShiftScheduleAssignmentId("p3-schedule"), new ShiftId("p3-shift"),
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
        var day = new ProductionDayId(site, new DateOnly(2026, 10, 18));
        var inputs = new List<PositionedMetricInputFact>();
        await using (var connection = fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            for (var i = 1; i <= 5; i++)
            {
                var planned = i == 2;
                var fact = new DurableMetricInputFact
                {
                    Id = new MetricInputFactId($"{machine.Value}-fact-{i}"),
                    Key = planned ? MetricInputFactKeys.PlannedProductionDuration : MetricInputFactKeys.RunningDuration,
                    Value = 1m, Unit = MetricInputFactUnits.Seconds,
                    StartsAtUtc = shift.StartsAtUtc.AddSeconds(i), EndsAtUtc = shift.StartsAtUtc.AddSeconds(i + 1),
                    CompanyId = new CompanyId("p3-company"), SiteId = site, MachineId = machine,
                    ShiftId = shift.ShiftId, ShiftScheduleAssignmentId = shift.ShiftScheduleAssignmentId,
                    ProductionLineId = new ProductionLineId("p3-line"), ProductionContextAssignmentId = new ProductionContextAssignmentId("p3-context"),
                    ProductionOrderId = new ProductionOrderId("p3-order"), OperationId = new OperationId("p3-operation"),
                    PartId = new PartId("p3-part"), OperatorId = new OperatorId("p3-operator"),
                    IsPlannedProductionTime = planned,
                    PlannedProductionScheduleAssignmentId = planned ? new PlannedProductionScheduleAssignmentId("p3-plan") : null,
                    SourceEligibilityIntervalId = new ProductionTimeEligibilityIntervalId($"p3-eligibility-{i}"),
                    SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId($"p3-activity-{i}"),
                    SourceQuantityEvidenceId = new ProductionQuantityEvidenceId($"p3-quantity-{i}")
                };
                inputs.Add(await SqlServerMetricInputTransactionWriter.AppendAsync(connection, transaction,
                    new DurableMetricInputAppend(stream, fact, shift, day), default));
            }
            await transaction.CommitAsync();
        }
        var cut = new MetricAggregationCheckpoint(processor, stream, new MetricInputPosition(4));
        var store = new SqlServerMetricAggregationStore(fixture.ConnectionString);
        await store.CommitAsync(new MetricAggregationCommit(processor, null, cut, inputs.Take(3).ToArray()), default);
        // Fact 4 is raw/unincorporated within A; 5 is incorporated after A.
        await store.CommitAsync(new MetricAggregationCommit(processor, cut,
            new(processor, stream, new MetricInputPosition(5)), [inputs[4]]), default);
        return new(machine, cut, shift, day, inputs);
    }

    private static async Task<string> SnapshotAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @Snapshot nvarchar(max) = (
            SELECT (SELECT * FROM dbo.MetricInputFact ORDER BY MetricInputFactRowId FOR JSON PATH, INCLUDE_NULL_VALUES) AS Facts,
                   (SELECT * FROM dbo.MetricAggregationContribution ORDER BY MetricAggregationProcessorRowId, Position FOR JSON PATH) AS Contributions,
                   (SELECT * FROM dbo.MetricAggregationCheckpoint ORDER BY MetricAggregationProcessorRowId FOR JSON PATH) AS Checkpoints,
                   (SELECT * FROM dbo.OperationalMetricProjection FOR JSON PATH) AS Projections,
                   (SELECT * FROM dbo.OperationalMetricConflictWitnessArchive FOR JSON PATH) AS Witnesses,
                   (SELECT * FROM dbo.OperationalMetricPublicationResultArchive FOR JSON PATH) AS Results,
                   (SELECT * FROM dbo.MetricAggregationRevision ORDER BY MetricAggregationProcessorRowId, Position FOR JSON PATH) AS Revisions,
                   (SELECT * FROM dbo.OperationalMetricProjectionCheckpoint ORDER BY OperationalMetricProjectionProcessorRowId FOR JSON PATH) AS ProjectionCheckpoints,
                   (SELECT * FROM dbo.OperationalMetricProjectionEvidence ORDER BY OperationalMetricProjectionEvidenceRowId FOR JSON PATH) AS Evidence,
                   (SELECT * FROM dbo.OperationalMetricProjectionManifest ORDER BY OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId FOR JSON PATH) AS Manifest,
                   (SELECT * FROM dbo.OperationalMetricResultWitnessReference FOR JSON PATH) AS WitnessReferences
            FOR JSON PATH);
            SELECT @Snapshot;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Seed(MachineId Machine, MetricAggregationCheckpoint Cut, ShiftOccurrenceId Shift,
        ProductionDayId Day, IReadOnlyList<PositionedMetricInputFact> Inputs);
}

/// <summary>Disposable current-schema database, created through the real migration engine.</summary>
public sealed class SqlContainmentAuthorityDatabaseFixture : IAsyncLifetime
{
    private SqlStartupIsolatedDatabase? _database;
    public string ConnectionString => _database!.ConnectionString;
    public SqlConnection CreateConnection() => new(ConnectionString);

    public async Task InitializeAsync()
    {
        _database = await SqlStartupIsolatedDatabase.CreateAsync();
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await SqlServerMigrationEngine.CreateDefault().ApplyAsync(connection, TimeSpan.FromMinutes(2), default);
        }
        catch
        {
            await _database.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}
