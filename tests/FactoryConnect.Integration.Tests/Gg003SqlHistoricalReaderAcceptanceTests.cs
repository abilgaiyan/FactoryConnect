using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

/// <summary>
/// Exact sanitized operand evidence plus explicitly synthetic padding/later facts.
/// Uses only the newly created class fixture database, never an RCA/factory restore.
/// Does not publish operational projections or exercise Stage B persistence.
/// </summary>
[Trait("Category", "SqlServerIntegration")]
public sealed class Gg003SqlHistoricalReaderAcceptanceTests(SqlServerTestDatabaseFixture fixture) :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly MachineId Machine = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly SiteId Site = new("rca-site");
    private static readonly MetricAggregationProcessorId Processor = new($"metric-aggregation:{Machine.Value}");
    private static readonly MetricInputStreamId Stream = MetricInputStreamId.ForMachine(Machine);
    private static readonly MetricAggregationCheckpoint Revision = new(Processor, Stream, new MetricInputPosition(534));
    private static readonly ShiftOccurrenceId Shift = new(Site,
        new ShiftScheduleAssignmentId("rca-shift-1-schedule"), new ShiftId("SHIFT-1"),
        new DateTimeOffset(2026, 10, 16, 0, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.Zero));
    private static readonly ProductionDayId Day = new(Site, new DateOnly(2026, 10, 16));

    [Fact]
    public async Task SqlHistorical534RetainsExactWitnessAndExcludesEveryLaterPositionThrough741()
    {
        Assert.StartsWith("FactoryConnect_FC026_", new SqlConnectionStringBuilder(fixture.ConnectionString).InitialCatalog, StringComparison.Ordinal);
        VerifyFixtureDigest();
        var originals = LoadInputs();
        Assert.Equal(34, originals.Length);
        var persisted = await SeedInputs(originals);
        var store = new SqlServerMetricAggregationStore(fixture.ConnectionString);
        // Only the reconciled operand contributions belong to this regression's
        // baseline aggregate cut. Synthetic padding exists in the input stream
        // solely to obtain the original positions through the normal allocator.
        await store.CommitAsync(new MetricAggregationCommit(Processor, null, Revision,
            persisted.Where(input => originals.Any(original => original.Position == input.Position)).ToArray()), CancellationToken.None);
        var beforeShift = await store.ReadAtRevisionAsync(Request(true), Revision, CancellationToken.None);
        var beforeDay = await store.ReadAtRevisionAsync(Request(false), Revision, CancellationToken.None);
        AssertExact534(beforeShift);
        AssertExact534(beforeDay);

        var firstLater = new MetricAggregationCheckpoint(Processor, Stream, new MetricInputPosition(535));
        await store.CommitAsync(new MetricAggregationCommit(Processor, Revision, firstLater,
            [persisted.Single(input => input.Position.Value == 535)]), CancellationToken.None);
        var latest = new MetricAggregationCheckpoint(Processor, Stream, new MetricInputPosition(741));
        var laterContributions = persisted.Where(input => input.Position.Value > 535).ToArray();
        Assert.Equal(206, laterContributions.Length);
        await store.CommitAsync(new MetricAggregationCommit(Processor, firstLater, latest, laterContributions), CancellationToken.None);

        // Reopen the SQL store: no in-memory snapshot or component adapter can
        // provide the witness, and current aggregates now intentionally differ.
        var reopened = new SqlServerMetricAggregationStore(fixture.ConnectionString);
        Assert.Equal(latest, await reopened.ReadCheckpointAsync(Processor, Stream, CancellationToken.None));
        foreach (var shift in new[] { true, false })
        {
            var historical = await reopened.ReadAtRevisionAsync(Request(shift), Revision, CancellationToken.None);
            AssertExact534(historical);
            var before = shift ? beforeShift : beforeDay;
            Assert.Equal(before.Components, historical.Components);
            Assert.Equal(before.AvailabilityScopeConflictEvidence, historical.AvailabilityScopeConflictEvidence);

            // Boundary control: 535 is included at its own committed revision,
            // while all 207 synthetic contributions affect the 741 snapshot.
            var at535 = await reopened.ReadAtRevisionAsync(Request(shift), firstLater, CancellationToken.None);
            Assert.Equal(2684.614157m, Running(at535).Aggregate.Value);
            var current = await reopened.ReadAtRevisionAsync(Request(shift), latest, CancellationToken.None);
            Assert.Equal(2787.614157m, Running(current).Aggregate.Value);
            Assert.Equal(135, Running(current).Aggregate.InputCount);
            Assert.Equal(2555.013355m, Planned(current).Aggregate.Value);
            Assert.Equal(106, Planned(current).Aggregate.InputCount);

            var key = Request(shift).EvaluationKey;
            var evaluator = new OperationalMetricEvaluator(new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All),
                new PinnedSqlReader(reopened), Processor, useAvailabilityContainmentV1: true);
            var evaluation = await evaluator.EvaluateAsync(key, CancellationToken.None);
            Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, evaluation.Status);
            Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, evaluation.ReasonCode);
            Assert.Null(evaluation.Value);
            Assert.Equal(historical.AvailabilityScopeConflictEvidence, evaluation.AvailabilityScopeConflictEvidence);
            Assert.Equal(Revision, evaluation.SourceRevision);

            var legacy = await reopened.ReadAtRevisionAsync(new OperationalMetricComponentSnapshotRequest(key, Processor,
                Request(shift).Operands), Revision, CancellationToken.None);
            Assert.Null(legacy.AvailabilityScopeConflictEvidence);
            Assert.Equal(historical.Components, legacy.Components);
        }

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*) FROM dbo.MetricAggregationContribution c
            JOIN dbo.MetricAggregationProcessor p ON p.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
            WHERE p.ProcessorKey = @Processor AND c.Position BETWEEN 535 AND 741;
            SELECT COUNT_BIG(*) FROM dbo.OperationalMetricProjection;
            """;
        command.Parameters.AddWithValue("@Processor", Processor.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(207L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
    }

    private async Task<PositionedMetricInputFact[]> SeedInputs(PositionedMetricInputFact[] originals)
    {
        var byPosition = originals.ToDictionary(input => input.Position.Value);
        var persisted = new List<PositionedMetricInputFact>();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        for (ulong position = 1; position <= 741; position++)
        {
            var fact = byPosition.TryGetValue(position, out var original) ? original.Fact : SyntheticFact(position);
            var input = await SqlServerMetricInputTransactionWriter.AppendAsync(connection, transaction,
                new DurableMetricInputAppend(Stream, fact, Shift, Day), CancellationToken.None);
            Assert.Equal(position, input.Position.Value);
            if (original is not null) Assert.Equal(original, input);
            persisted.Add(input);
        }
        await transaction.CommitAsync();
        return persisted.ToArray();
    }

    private static DurableMetricInputFact SyntheticFact(ulong position)
    {
        var later = position > 534;
        var planned = position % 2 == 0;
        var start = Shift.StartsAtUtc.AddSeconds(position);
        return new DurableMetricInputFact
        {
            Id = new MetricInputFactId($"synthetic-sql-acceptance-{position}"),
            Key = !later ? "sql-acceptance.padding" : planned ? MetricInputFactKeys.PlannedProductionDuration : MetricInputFactKeys.RunningDuration,
            Value = 1m, Unit = MetricInputFactUnits.Seconds, StartsAtUtc = start, EndsAtUtc = start.AddSeconds(1),
            CompanyId = new CompanyId("rca-company"), SiteId = Site, MachineId = Machine,
            ShiftId = Shift.ShiftId, ShiftScheduleAssignmentId = Shift.ShiftScheduleAssignmentId,
            IsPlannedProductionTime = planned,
            PlannedProductionScheduleAssignmentId = planned ? new PlannedProductionScheduleAssignmentId("synthetic-sql-plan") : null,
            SourceEligibilityIntervalId = new ProductionTimeEligibilityIntervalId($"synthetic-sql-eligibility-{position}"),
            SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId($"synthetic-sql-activity-{position}"),
        };
    }

    private static OperationalMetricComponentSnapshotRequest Request(bool shift) => new(new OperationalMetricEvaluationKey(Machine,
        shift ? new OperationalMetricPeriodId.Shift(Shift) : new OperationalMetricPeriodId.ProductionDay(Day),
        BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned), Processor,
        BuiltInOperationalMetricDefinitions.All.Single(definition => definition.Id == BuiltInOperationalMetricDefinitions.AvailabilityId).Operands,
        includeAvailabilityScopeConflictEvidence: true);

    private static OperationalMetricComponent Running(OperationalMetricComponentSnapshot snapshot) =>
        snapshot.Components.Single(component => component.SourceIdentity.ComponentKey == MetricInputFactKeys.RunningDuration);
    private static OperationalMetricComponent Planned(OperationalMetricComponentSnapshot snapshot) =>
        snapshot.Components.Single(component => component.SourceIdentity.ComponentKey == MetricInputFactKeys.PlannedProductionDuration);

    private static void AssertExact534(OperationalMetricComponentSnapshot snapshot)
    {
        Assert.Equal(Revision, snapshot.Revision);
        Assert.Equal(2683.614157m, Running(snapshot).Aggregate.Value);
        Assert.Equal(2452.013355m, Planned(snapshot).Aggregate.Value);
        Assert.Equal(31, Running(snapshot).Aggregate.InputCount);
        Assert.Equal(3, Planned(snapshot).Aggregate.InputCount);
        var witness = Assert.IsType<AvailabilityScopeConflictEvidenceV1>(snapshot.AvailabilityScopeConflictEvidence);
        Assert.Equal(Revision, witness.SourceRevision);
        Assert.Equal(380UL, witness.Contribution.Position.Value);
        Assert.Equal(new MetricInputFactId("rca-fact-380"), witness.Contribution.Fact.Id);
        Assert.Equal(new ProductionTimeEligibilityIntervalId("rca-eligibility-380"), witness.Contribution.Fact.SourceEligibilityIntervalId);
        Assert.Equal(new ContextualizedActivityIntervalId("rca-activity-380"), witness.Contribution.Fact.SourceContextualizedActivityIntervalId);
        Assert.Equal(259.637610m, witness.Contribution.Fact.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T02:32:25.791360Z", CultureInfo.InvariantCulture), witness.Contribution.Fact.StartsAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T02:36:45.428970Z", CultureInfo.InvariantCulture), witness.Contribution.Fact.EndsAtUtc);
        Assert.Equal(false, witness.Contribution.Fact.IsPlannedProductionTime);
        Assert.Null(witness.Contribution.Fact.PlannedProductionScheduleAssignmentId);
        Assert.Equal(AvailabilityContainmentV1.PlannedProductionOperandContractId, witness.PlannedProductionOperandContractId);
        Assert.Equal(Running(snapshot).SourceIdentity, witness.Numerator.SourceIdentity);
        Assert.Equal(Planned(snapshot).SourceIdentity, witness.Denominator.SourceIdentity);
        Assert.Equal(Running(snapshot).Aggregate, witness.Numerator.Aggregate);
        Assert.Equal(Planned(snapshot).Aggregate, witness.Denominator.Aggregate);
    }

    private static void VerifyFixtureDigest()
    {
        var assembly = typeof(Gg003SqlHistoricalReaderAcceptanceTests).Assembly;
        using var stream = assembly.GetManifestResourceStream("FactoryConnect.Integration.Tests.TestData.gg003-revision-534-components.json")!;
        using var text = new StreamReader(stream);
        var canonical = text.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        using var manifestStream = assembly.GetManifestResourceStream("FactoryConnect.Integration.Tests.TestData.gg003-revision-534-source-cut.json")!;
        using var manifest = JsonDocument.Parse(manifestStream);
        Assert.Equal(manifest.RootElement.GetProperty("fixtureSha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private sealed class PinnedSqlReader(IRevisionedOperationalMetricComponentSnapshotReader store) : IOperationalMetricComponentSnapshotReader
    {
        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(OperationalMetricComponentSnapshotRequest request, CancellationToken cancellationToken) =>
            store.ReadAtRevisionAsync(request, Revision, cancellationToken);
    }

    private static PositionedMetricInputFact[] LoadInputs()
    {
        using var stream = typeof(Gg003SqlHistoricalReaderAcceptanceTests).Assembly.GetManifestResourceStream(
            "FactoryConnect.Integration.Tests.TestData.gg003-revision-534-components.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().Select(x => new PositionedMetricInputFact(Stream,
            new MetricInputPosition(x.GetProperty("Position").GetUInt64()), new DurableMetricInputFact
            {
                Id = new MetricInputFactId(x.GetProperty("FactId").GetString()!),
                Key = x.GetProperty("MetricInputKey").GetString()!,
                Value = decimal.Parse(x.GetProperty("MetricValue").GetString()!, CultureInfo.InvariantCulture),
                Unit = x.GetProperty("Unit").GetString()!,
                StartsAtUtc = x.GetProperty("StartsAtUtc").GetDateTimeOffset(),
                EndsAtUtc = x.GetProperty("EndsAtUtc").GetDateTimeOffset(),
                CompanyId = new CompanyId(x.GetProperty("CompanyId").GetString()!),
                SiteId = Site,
                ProductionLineId = new ProductionLineId(x.GetProperty("ProductionLineId").GetString()!),
                ProductionContextAssignmentId = new ProductionContextAssignmentId(x.GetProperty("ProductionContextAssignmentId").GetString()!),
                MachineId = Machine,
                ShiftId = Shift.ShiftId,
                ShiftScheduleAssignmentId = Shift.ShiftScheduleAssignmentId,
                IsPlannedProductionTime = x.GetProperty("IsPlannedProductionTime").GetBoolean(),
                PlannedProductionScheduleAssignmentId = x.TryGetProperty("PlannedProductionScheduleAssignmentId", out var planned)
                    ? new PlannedProductionScheduleAssignmentId(planned.GetString()!) : null,
                SourceContextualizedActivityIntervalId = new ContextualizedActivityIntervalId(
                    x.GetProperty("SourceContextualizedActivityIntervalId").GetString()!),
                SourceEligibilityIntervalId = new ProductionTimeEligibilityIntervalId(
                    x.GetProperty("SourceEligibilityIntervalId").GetString()!),
            }, Shift, Day)).ToArray();
    }
}
