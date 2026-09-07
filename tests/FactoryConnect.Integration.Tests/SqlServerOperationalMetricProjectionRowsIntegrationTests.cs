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

    public SqlServerOperationalMetricProjectionRowsIntegrationTests(SqlServerTestDatabaseFixture fixture)
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
        Assert.Equal("-1E-28", CanonicalDecimalTextV1Codec.Serialize(-0.0000000000000000000000000001m));
        Assert.Equal("79228162514264337593543950335", CanonicalDecimalTextV1Codec.Serialize(decimal.MaxValue));
    }

    [Fact]
    public void EvaluationKeyV1EncodesFrozenEnvelopeAndStringBoundaries()
    {
        var machineId = new MachineId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var key = CreateProductionDayKey(machineId, new string('x', 256), "1");

        var encoded = OperationalMetricEvaluationKeyV1Codec.Encode(key);

        Assert.Equal("4643454B010001", Convert.ToHexString(encoded.AsSpan(0, 7)));
        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(encoded.AsSpan(7, 16)));
        Assert.Equal(32, OperationalMetricEvaluationKeyV1Codec.ComputeHash(encoded).Length);

        var oversized = CreateProductionDayKey(machineId, new string('x', 257), "1");
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricEvaluationKeyV1Codec.Encode(oversized));
    }

    [Fact]
    public async Task InitialInsertPersistsCanonicalIdentityAndProjectionState()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var key = CreateShiftKey(source.MachineId, "availability", "1");
        var projection = CreateCalculated(processorId, key, source.FirstCheckpoint, 0.00001m, "ratio");
        var commit = CreateCommit(processorId, null, source.FirstCheckpoint, projection);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        SqlServerOperationalMetricProjectionRowResolution? resolution = null;

        await sut.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
            {
                resolution = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(
                    context, commit, cancellationToken);
            },
            CancellationToken.None);

        Assert.NotNull(resolution);
        var resolved = Assert.Single(resolution.PublishedRows);
        Assert.Empty(resolution.ObsoleteProjectionRowIds);
        var row = await ReadProjectionRowAsync(resolved.ProjectionRowId);
        Assert.Equal((short)1, row.CodecVersion);
        Assert.Equal(resolved.EvaluationKeyHash, row.Hash);
        Assert.Equal(resolved.EvaluationKeyBinary, row.Binary);
        Assert.Equal("1E-05", row.MetricValue);
        Assert.Equal("ratio", row.Unit);
        Assert.Equal(source.FirstCheckpoint.Position, row.SourceRevisionPosition);
        Assert.Equal(StringOrderKeyV2Codec.Encode("SITE-1"), row.PeriodSiteOrderKey);
        Assert.Equal(StringOrderKeyV2Codec.Encode("availability"), row.MetricKeyOrderKey);
    }

    [Fact]
    public async Task AdvanceSameKeyReplacesStateAndPreservesProjectionRowId()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var key = CreateShiftKey(source.MachineId, "performance", "1");
        var firstProjection = CreateCalculated(processorId, key, source.FirstCheckpoint, 0.5m, "ratio");
        var firstCommit = CreateCommit(processorId, null, source.FirstCheckpoint, firstProjection);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        long firstRowId = 0;
        await sut.ExecuteAsync(firstCommit, async (context, ct) =>
        {
            var result = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, firstCommit, ct);
            firstRowId = Assert.Single(result.PublishedRows).ProjectionRowId;
        }, CancellationToken.None);

        var secondProjection = CreateCalculated(processorId, key, source.SecondCheckpoint, 0.75m, "ratio-v2");
        var secondCommit = CreateCommit(processorId, firstCommit.ProposedCheckpoint, source.SecondCheckpoint, secondProjection);
        long secondRowId = 0;
        await sut.ExecuteAsync(secondCommit, async (context, ct) =>
        {
            var result = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, secondCommit, ct);
            secondRowId = Assert.Single(result.PublishedRows).ProjectionRowId;
        }, CancellationToken.None);

        Assert.Equal(firstRowId, secondRowId);
        var row = await ReadProjectionRowAsync(secondRowId);
        Assert.Equal("0.75", row.MetricValue);
        Assert.Equal("ratio-v2", row.Unit);
        Assert.Equal(source.SecondCheckpoint.Position, row.SourceRevisionPosition);
    }

    [Fact]
    public async Task ObsoleteRowsAreReportedButRemainForC4DependencySafeDeletion()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var keepKey = CreateShiftKey(source.MachineId, "availability", "1");
        var removeKey = CreateShiftKey(source.MachineId, "performance", "1");
        var firstCommit = CreateCommit(
            processorId,
            null,
            source.FirstCheckpoint,
            CreateCalculated(processorId, keepKey, source.FirstCheckpoint, 0.5m, "ratio"),
            CreateCalculated(processorId, removeKey, source.FirstCheckpoint, 0.6m, "ratio"));
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await sut.ExecuteAsync(firstCommit, async (context, ct) =>
            _ = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, firstCommit, ct), CancellationToken.None);

        var secondCommit = CreateCommit(
            processorId,
            firstCommit.ProposedCheckpoint,
            source.SecondCheckpoint,
            CreateCalculated(processorId, keepKey, source.SecondCheckpoint, 0.7m, "ratio"));
        SqlServerOperationalMetricProjectionRowResolution? resolution = null;
        await sut.ExecuteAsync(secondCommit, async (context, ct) =>
        {
            resolution = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, secondCommit, ct);
        }, CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.Single(resolution.ObsoleteProjectionRowIds);
        Assert.Equal(2, await CountProjectionRowsAsync(processorId));
    }

    [Fact]
    public async Task EqualRevisionReplayValidatesWithoutMutatingProjectionRow()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var key = CreateProductionDayKey(source.MachineId, "oee", "1");
        var projection = CreateCalculated(processorId, key, source.FirstCheckpoint, 0.8125m, "ratio");
        var commit = CreateCommit(processorId, null, source.FirstCheckpoint, projection);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        long firstRowId = 0;
        await sut.ExecuteAsync(commit, async (context, ct) =>
        {
            var result = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, commit, ct);
            firstRowId = Assert.Single(result.PublishedRows).ProjectionRowId;
        }, CancellationToken.None);

        await sut.ExecuteAsync(commit, async (context, ct) =>
        {
            Assert.Equal(SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed, context.Mode);
            var result = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, commit, ct);
            Assert.Equal(firstRowId, Assert.Single(result.PublishedRows).ProjectionRowId);
        }, CancellationToken.None);

        Assert.Equal(1, await CountProjectionRowsAsync(processorId));
    }

    [Fact]
    public async Task EqualRevisionReplayRejectsCoreStateMismatchWithoutRepair()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var key = CreateProductionDayKey(source.MachineId, "quality", "1");
        var projection = CreateCalculated(processorId, key, source.FirstCheckpoint, 0.95m, "ratio");
        var commit = CreateCommit(processorId, null, source.FirstCheckpoint, projection);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        long rowId = 0;
        await sut.ExecuteAsync(commit, async (context, ct) =>
        {
            var result = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, commit, ct);
            rowId = Assert.Single(result.PublishedRows).ProjectionRowId;
        }, CancellationToken.None);
        await SetProjectionUnitAsync(rowId, "tampered");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecuteAsync(
            commit,
            (context, ct) => SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, commit, ct),
            CancellationToken.None));

        Assert.Equal("tampered", (await ReadProjectionRowAsync(rowId)).Unit);
    }

    [Fact]
    public async Task BodyFailureRollsBackProjectionInsertAndCheckpoint()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var key = CreateShiftKey(source.MachineId, "availability", "1");
        var projection = CreateCalculated(processorId, key, source.FirstCheckpoint, 0.9m, "ratio");
        var commit = CreateCommit(processorId, null, source.FirstCheckpoint, projection);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecuteAsync(
            commit,
            async (context, ct) =>
            {
                _ = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, commit, ct);
                throw new InvalidOperationException("Injected C.3 failure.");
            },
            CancellationToken.None));

        Assert.Equal(0, await CountProjectionRowsAsync(processorId));
        Assert.Null(await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None));
    }

    [Fact]
    public async Task AffectedProjectionHashesAreReturnedInAscendingByteOrder()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var projections = new[]
        {
            CreateCalculated(processorId, CreateShiftKey(source.MachineId, "z", "1"), source.FirstCheckpoint, 1m, "ratio"),
            CreateCalculated(processorId, CreateShiftKey(source.MachineId, "a", "1"), source.FirstCheckpoint, 2m, "ratio"),
            CreateCalculated(processorId, CreateShiftKey(source.MachineId, "m", "1"), source.FirstCheckpoint, 3m, "ratio"),
        };
        var commit = CreateCommit(processorId, null, source.FirstCheckpoint, projections);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        IReadOnlyList<byte[]>? hashes = null;

        await sut.ExecuteAsync(commit, async (context, ct) =>
        {
            var resolution = await SqlServerOperationalMetricProjectionRows.ResolveAndPersistAsync(context, commit, ct);
            hashes = resolution.LockedHashes;
        }, CancellationToken.None);

        Assert.NotNull(hashes);
        for (var index = 1; index < hashes.Count; index++)
        {
            Assert.True(hashes[index - 1].AsSpan().SequenceCompareTo(hashes[index]) < 0);
        }
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
            new OperationalMetricProjectionBatchManifest(projections.Select(static projection => projection.Key)));
        return new OperationalMetricProjectionCommit(processorId, expected, proposed, projections);
    }

    private static OperationalMetricProjection CreateCalculated(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint sourceRevision,
        decimal value,
        string unit) =>
        new(processorId, key, OperationalMetricEvaluationStatus.Calculated, value, unit, null, null, sourceRevision);

    private static OperationalMetricEvaluationKey CreateShiftKey(MachineId machineId, string metricKey, string version)
    {
        var period = new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(
            new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero)));
        return new OperationalMetricEvaluationKey(
            machineId,
            period,
            new OperationalMetricDefinitionId(metricKey, version),
            OperationalMetricEvaluationContextKey.Unpartitioned);
    }

    private static OperationalMetricEvaluationKey CreateProductionDayKey(MachineId machineId, string metricKey, string version)
    {
        var period = new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("SITE-1"), new DateOnly(2026, 9, 7)));
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
        var firstFact = await inputStore.AppendAsync(CreateAppend(machineId, $"projection-source-{Guid.NewGuid():N}", 0), CancellationToken.None);
        var secondFact = await inputStore.AppendAsync(CreateAppend(machineId, $"projection-source-{Guid.NewGuid():N}", 1), CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId($"projection-source-{Guid.NewGuid():N}");
        var durableCheckpoint = new MetricAggregationCheckpoint(aggregationProcessorId, firstFact.StreamId, secondFact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(new MetricAggregationCommit(aggregationProcessorId, null, durableCheckpoint, []), CancellationToken.None);
        return new SourceFixture(
            machineId,
            new MetricAggregationCheckpoint(aggregationProcessorId, firstFact.StreamId, firstFact.Position),
            new MetricAggregationCheckpoint(aggregationProcessorId, firstFact.StreamId, secondFact.Position));
    }

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId, int minute)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
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
            new ShiftOccurrenceId(siteId, scheduleId, shiftId, occurrenceStart, occurrenceStart.AddHours(8)),
            new ProductionDayId(siteId, DateOnly.FromDateTime(occurrenceStart.UtcDateTime)));
    }

    private async Task<int> CountProjectionRowsAsync(OperationalMetricProjectionProcessorId processorId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.OperationalMetricProjection AS p INNER JOIN dbo.OperationalMetricProjectionProcessor AS pp " +
            "ON pp.OperationalMetricProjectionProcessorRowId = p.OperationalMetricProjectionProcessorRowId WHERE pp.ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(processorId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<ProjectionRowSnapshot> ReadProjectionRowAsync(long rowId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EvaluationKeyCodecVersion, EvaluationKeyHash, EvaluationKeyBinary, MetricValue, Unit, SourceRevisionPosition, PeriodSiteOrderKey, MetricKeyOrderKey " +
            "FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionRowId = @RowId;";
        command.Parameters.Add("@RowId", SqlDbType.BigInt).Value = rowId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProjectionRowSnapshot(
            reader.GetInt16(0), (byte[])reader[1], (byte[])reader[2], reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
            new MetricInputPosition(SqlServerUInt64.Materialize(reader.GetDecimal(5))), (byte[])reader[6], (byte[])reader[7]);
    }

    private async Task SetProjectionUnitAsync(long rowId, string unit)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.OperationalMetricProjection SET Unit = @Unit WHERE OperationalMetricProjectionRowId = @RowId;";
        command.Parameters.Add("@Unit", SqlDbType.NVarChar, 128).Value = unit;
        command.Parameters.Add("@RowId", SqlDbType.BigInt).Value = rowId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed record SourceFixture(MachineId MachineId, MetricAggregationCheckpoint FirstCheckpoint, MetricAggregationCheckpoint SecondCheckpoint);
    private sealed record ProjectionRowSnapshot(short CodecVersion, byte[] Hash, byte[] Binary, string? MetricValue, string Unit, MetricInputPosition SourceRevisionPosition, byte[] PeriodSiteOrderKey, byte[] MetricKeyOrderKey);
}
