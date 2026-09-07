using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionRowsIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionRowsIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CanonicalDecimalTextV1MatchesFrozenNotationBoundary()
    {
        Assert.Equal("0", CanonicalDecimalTextV1Codec.Serialize(0.000m));
        Assert.Equal("1", CanonicalDecimalTextV1Codec.Serialize(1.00m));
        Assert.Equal("0.0001", CanonicalDecimalTextV1Codec.Serialize(0.0001m));
        Assert.Equal("1E-05", CanonicalDecimalTextV1Codec.Serialize(0.00001m));
        Assert.Equal("1.2345E-05", CanonicalDecimalTextV1Codec.Serialize(0.000012345m));
        Assert.Equal(
            "-1E-28",
            CanonicalDecimalTextV1Codec.Serialize(-0.0000000000000000000000000001m));
        Assert.Equal(
            "79228162514264337593543950335",
            CanonicalDecimalTextV1Codec.Serialize(decimal.MaxValue));
    }

    [Fact]
    public void EvaluationKeyV1EncodesFrozenEnvelopeAndStringBoundaries()
    {
        var machineId = new MachineId(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var key = CreateProductionDayKey(machineId, new string('x', 256), "1");

        var encoded = OperationalMetricEvaluationKeyV1Codec.Encode(key);

        Assert.Equal(
            "4643454B010001",
            Convert.ToHexString(encoded.AsSpan(0, 7)));
        Assert.Equal(
            "00112233445566778899AABBCCDDEEFF",
            Convert.ToHexString(encoded.AsSpan(7, 16)));
        Assert.Equal(
            32,
            OperationalMetricEvaluationKeyV1Codec.ComputeHash(encoded).Length);

        var oversized = CreateProductionDayKey(
            machineId,
            new string('x', 257),
            "1");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OperationalMetricEvaluationKeyV1Codec.Encode(oversized));
    }

    [Fact]
    public async Task PrepareInitialCommitLocksAndValidatesWithoutProjectionDml()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"projection-{Guid.NewGuid():N}");
        var key = CreateShiftKey(source.MachineId, "availability", "1");
        var projection = CreateCalculated(
            processorId,
            key,
            source.FirstCheckpoint,
            0.00001m,
            "ratio");
        var commit = CreateCommit(
            processorId,
            null,
            source.FirstCheckpoint,
            projection);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);

        await Assert.ThrowsAsync<PreparationInspectionCompleteException>(
            () => sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    var plan = await SqlServerOperationalMetricProjectionRows.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);

                    var prepared = Assert.Single(plan.ProposedRows);
                    Assert.Null(prepared.ExistingProjectionRowId);
                    Assert.Empty(plan.ObsoleteProjectionRowIds);
                    Assert.Single(plan.LockedHashes);
                    Assert.Equal(
                        prepared.EvaluationKeyHash,
                        plan.LockedHashes[0]);
                    Assert.Equal(
                        "1E-05",
                        prepared.WriteModel.MetricValue);
                    Assert.Equal(
                        StringOrderKeyV2Codec.Encode("SITE-1"),
                        prepared.WriteModel.PeriodSiteOrderKey);
                    Assert.Equal(
                        StringOrderKeyV2Codec.Encode("availability"),
                        prepared.WriteModel.MetricKeyOrderKey);

                    Assert.Equal(
                        0,
                        await CountProjectionRowsInTransactionAsync(
                            context,
                            cancellationToken));
                    Assert.Equal(
                        0,
                        await CountManifestRowsInTransactionAsync(
                            context,
                            cancellationToken));
                    Assert.Equal(
                        0,
                        await CountEvidenceRowsInTransactionAsync(
                            context,
                            cancellationToken));

                    throw new PreparationInspectionCompleteException();
                },
                CancellationToken.None));

        Assert.Equal(0, await CountProjectionRowsAsync(processorId));
        Assert.Null(
            await sut.ReadCheckpointHeaderAsync(
                processorId,
                CancellationToken.None));
    }

    [Fact]
    public async Task PrepareAffectedProjectionHashesAreLockedInAscendingByteOrder()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"projection-{Guid.NewGuid():N}");
        var projections = new[]
        {
            CreateCalculated(
                processorId,
                CreateShiftKey(source.MachineId, "z", "1"),
                source.FirstCheckpoint,
                1m,
                "ratio"),
            CreateCalculated(
                processorId,
                CreateShiftKey(source.MachineId, "a", "1"),
                source.FirstCheckpoint,
                2m,
                "ratio"),
            CreateCalculated(
                processorId,
                CreateShiftKey(source.MachineId, "m", "1"),
                source.FirstCheckpoint,
                3m,
                "ratio"),
        };
        var commit = CreateCommit(
            processorId,
            null,
            source.FirstCheckpoint,
            projections);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);

        await Assert.ThrowsAsync<PreparationInspectionCompleteException>(
            () => sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    var plan = await SqlServerOperationalMetricProjectionRows.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);

                    Assert.Equal(3, plan.ProposedRows.Count);
                    Assert.All(
                        plan.ProposedRows,
                        static row => Assert.Null(row.ExistingProjectionRowId));
                    Assert.Equal(3, plan.LockedHashes.Count);

                    for (var index = 1; index < plan.LockedHashes.Count; index++)
                    {
                        Assert.True(
                            plan.LockedHashes[index - 1]
                                .AsSpan()
                                .SequenceCompareTo(plan.LockedHashes[index]) < 0);
                    }

                    Assert.Equal(
                        0,
                        await CountProjectionRowsInTransactionAsync(
                            context,
                            cancellationToken));

                    throw new PreparationInspectionCompleteException();
                },
                CancellationToken.None));

        Assert.Equal(0, await CountProjectionRowsAsync(processorId));
        Assert.Null(
            await sut.ReadCheckpointHeaderAsync(
                processorId,
                CancellationToken.None));
    }

    [Fact]
    public async Task PrepareFailureRollsBackProcessorAndLeavesNoPublishedState()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"projection-{Guid.NewGuid():N}");
        var key = CreateProductionDayKey(source.MachineId, "oee", "1");
        var projection = CreateCalculated(
            processorId,
            key,
            source.FirstCheckpoint,
            0.8125m,
            "ratio");
        var commit = CreateCommit(
            processorId,
            null,
            source.FirstCheckpoint,
            projection);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ExecuteAsync(
                commit,
                async (context, cancellationToken) =>
                {
                    _ = await SqlServerOperationalMetricProjectionRows.PrepareAsync(
                        context,
                        commit,
                        cancellationToken);

                    Assert.Equal(
                        0,
                        await CountProjectionRowsInTransactionAsync(
                            context,
                            cancellationToken));

                    throw new InvalidOperationException(
                        "Injected failure after C.3 preparation.");
                },
                CancellationToken.None));

        Assert.Equal(0, await CountProjectionRowsAsync(processorId));
        Assert.Null(
            await sut.ReadCheckpointHeaderAsync(
                processorId,
                CancellationToken.None));
    }

    private static OperationalMetricProjectionCommit CreateCommit(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricProjectionCheckpoint? expected,
        MetricAggregationCheckpoint sourceRevision,
        params OperationalMetricProjection[] projections)
    {
        var proposed = new OperationalMetricProjectionCheckpoint(
            processorId,
            sourceRevision,
            new OperationalMetricProjectionBatchManifest(
                projections.Select(static projection => projection.Key)));

        return new OperationalMetricProjectionCommit(
            processorId,
            expected,
            proposed,
            projections);
    }

    private static OperationalMetricProjection CreateCalculated(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint sourceRevision,
        decimal value,
        string unit) =>
        new(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            value,
            unit,
            null,
            null,
            sourceRevision);

    private static OperationalMetricEvaluationKey CreateShiftKey(
        MachineId machineId,
        string metricKey,
        string version)
    {
        var period = new OperationalMetricPeriodId.Shift(
            new ShiftOccurrenceId(
                new SiteId("SITE-1"),
                new ShiftScheduleAssignmentId("SCHEDULE-A"),
                new ShiftId("SHIFT-A"),
                new DateTimeOffset(
                    2026,
                    9,
                    7,
                    6,
                    0,
                    0,
                    TimeSpan.Zero),
                new DateTimeOffset(
                    2026,
                    9,
                    7,
                    14,
                    0,
                    0,
                    TimeSpan.Zero)));

        return new OperationalMetricEvaluationKey(
            machineId,
            period,
            new OperationalMetricDefinitionId(metricKey, version),
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private static OperationalMetricEvaluationKey CreateProductionDayKey(
        MachineId machineId,
        string metricKey,
        string version)
    {
        var period = new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(
                new SiteId("SITE-1"),
                new DateOnly(2026, 9, 7)));

        return new OperationalMetricEvaluationKey(
            machineId,
            period,
            new OperationalMetricDefinitionId(metricKey, version),
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var firstFact = await inputStore.AppendAsync(
            CreateAppend(
                machineId,
                $"projection-source-{Guid.NewGuid():N}",
                0),
            CancellationToken.None);
        var secondFact = await inputStore.AppendAsync(
            CreateAppend(
                machineId,
                $"projection-source-{Guid.NewGuid():N}",
                1),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"projection-source-{Guid.NewGuid():N}");
        var durableCheckpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            firstFact.StreamId,
            secondFact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(
            _fixture.ConnectionString);

        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                null,
                durableCheckpoint,
                []),
            CancellationToken.None);

        return new SourceFixture(
            machineId,
            new MetricAggregationCheckpoint(
                aggregationProcessorId,
                firstFact.StreamId,
                firstFact.Position),
            new MetricAggregationCheckpoint(
                aggregationProcessorId,
                firstFact.StreamId,
                secondFact.Position));
    }

    private static DurableMetricInputAppend CreateAppend(
        MachineId machineId,
        string factId,
        int minute)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart = new DateTimeOffset(
            2026,
            9,
            7,
            6,
            0,
            0,
            TimeSpan.Zero);
        var factStart = occurrenceStart.AddMinutes(minute);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = 1m,
            Unit = "seconds",
            StartsAtUtc = factStart,
            EndsAtUtc = factStart.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = siteId,
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            ShiftId = shiftId,
            ShiftScheduleAssignmentId = scheduleId,
        };

        return new DurableMetricInputAppend(
            MetricInputStreamId.ForMachine(machineId),
            fact,
            new ShiftOccurrenceId(
                siteId,
                scheduleId,
                shiftId,
                occurrenceStart,
                occurrenceStart.AddHours(8)),
            new ProductionDayId(
                siteId,
                DateOnly.FromDateTime(occurrenceStart.UtcDateTime)));
    }

    private static async Task<int> CountProjectionRowsInTransactionAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.OperationalMetricProjection " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            context.ProjectionProcessorRowId;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountManifestRowsInTransactionAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.OperationalMetricProjectionManifest " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            context.ProjectionProcessorRowId;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountEvidenceRowsInTransactionAsync(
        SqlServerOperationalMetricProjectionCommitContext context,
        CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.OperationalMetricProjectionEvidence AS e " +
            "INNER JOIN dbo.OperationalMetricProjection AS p " +
            "ON p.OperationalMetricProjectionRowId = e.OperationalMetricProjectionRowId " +
            "WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            context.ProjectionProcessorRowId;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> CountProjectionRowsAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.OperationalMetricProjection AS p " +
            "INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp " +
            "ON pp.OperationalMetricProjectionProcessorRowId = p.OperationalMetricProjectionProcessorRowId " +
            "WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add(
            "@ProcessorKeyBinary",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(processorId.Value);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint FirstCheckpoint,
        MetricAggregationCheckpoint SecondCheckpoint);

    private sealed class PreparationInspectionCompleteException : Exception;
}
