using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionCommitTransactionIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionCommitTransactionIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UnknownCheckpointReadDoesNotCreateProjectionProcessor()
    {
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        var header = await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);

        Assert.Null(header);
        Assert.Equal(0, await CountProjectionProcessorsAsync(processorId));
    }

    [Fact]
    public async Task InitialEmptyCommitCreatesProcessorAndCheckpointInsideSerializableTransaction()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var commit = CreateEmptyCommit(processorId, expected: null, source.FirstCheckpoint);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        SqlServerOperationalMetricProjectionCommitMode? observedMode = null;

        await sut.ExecuteAsync(
            commit,
            (context, _) =>
            {
                observedMode = context.Mode;
                Assert.Equal(IsolationLevel.Serializable, context.Transaction.IsolationLevel);
                Assert.Null(context.DurablePosition);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var header = await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        Assert.NotNull(header);
        Assert.Equal(SqlServerOperationalMetricProjectionCommitMode.Advance, observedMode);
        Assert.Equal(source.FirstCheckpoint.Position, header.Position);
        Assert.Equal(1, await CountProjectionProcessorsAsync(processorId));
        await AssertProcessorKeyV2Async(processorId);
    }

    [Fact]
    public async Task AdvancingCommitMovesCheckpointAfterBodyCompletes()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var first = CreateEmptyCommit(processorId, expected: null, source.FirstCheckpoint);
        await sut.ExecuteAsync(first, NoOpBody, CancellationToken.None);
        var expected = first.ProposedCheckpoint;
        var second = CreateEmptyCommit(processorId, expected, source.SecondCheckpoint);
        MetricInputPosition? positionInsideBody = null;

        await sut.ExecuteAsync(
            second,
            async (context, cancellationToken) =>
            {
                positionInsideBody = await ReadCheckpointPositionAsync(
                    context.Connection,
                    context.Transaction,
                    context.ProjectionProcessorRowId,
                    cancellationToken);
            },
            CancellationToken.None);

        Assert.Equal(source.FirstCheckpoint.Position, positionInsideBody);
        var header = await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        Assert.NotNull(header);
        Assert.Equal(source.SecondCheckpoint.Position, header.Position);
    }

    [Fact]
    public async Task StaleExpectedCheckpointFailsWithoutMutation()
    {
        var source = await CreateSourceAsync(includeThird: true);
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var first = CreateEmptyCommit(processorId, expected: null, source.FirstCheckpoint);
        await sut.ExecuteAsync(first, NoOpBody, CancellationToken.None);
        var second = CreateEmptyCommit(processorId, first.ProposedCheckpoint, source.SecondCheckpoint);
        await sut.ExecuteAsync(second, NoOpBody, CancellationToken.None);
        var stale = CreateEmptyCommit(processorId, first.ProposedCheckpoint, source.ThirdCheckpoint!);
        var bodyCalled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ExecuteAsync(
                stale,
                (_, _) =>
                {
                    bodyCalled = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.False(bodyCalled);
        var header = await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        Assert.NotNull(header);
        Assert.Equal(source.SecondCheckpoint.Position, header.Position);
    }

    [Fact]
    public async Task RetryAtAlreadyProposedPositionEntersReconciliationModeWithoutCheckpointRewrite()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var commit = CreateEmptyCommit(processorId, expected: null, source.FirstCheckpoint);
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await sut.ExecuteAsync(commit, NoOpBody, CancellationToken.None);
        SqlServerOperationalMetricProjectionCommitMode? observedMode = null;

        await sut.ExecuteAsync(
            commit,
            (context, _) =>
            {
                observedMode = context.Mode;
                Assert.Equal(source.FirstCheckpoint.Position, context.DurablePosition);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed, observedMode);
        var header = await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        Assert.NotNull(header);
        Assert.Equal(source.FirstCheckpoint.Position, header.Position);
    }

    [Fact]
    public async Task ExistingProjectionProcessorRejectsDifferentSourceBinding()
    {
        var firstSource = await CreateSourceAsync();
        var secondSource = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await sut.ExecuteAsync(
            CreateEmptyCommit(processorId, expected: null, firstSource.FirstCheckpoint),
            NoOpBody,
            CancellationToken.None);

        var conflicting = CreateEmptyCommit(
            processorId,
            expected: null,
            secondSource.FirstCheckpoint);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ExecuteAsync(conflicting, NoOpBody, CancellationToken.None));

        var header = await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        Assert.NotNull(header);
        Assert.Equal(firstSource.FirstCheckpoint.Position, header.Position);
    }

    [Fact]
    public async Task BodyFailureRollsBackInitialProcessorAndCheckpoint()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var sut = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ExecuteAsync(
                CreateEmptyCommit(processorId, expected: null, source.FirstCheckpoint),
                (_, _) => throw new InvalidOperationException("Injected C.2 failure."),
                CancellationToken.None));

        Assert.Null(await sut.ReadCheckpointHeaderAsync(processorId, CancellationToken.None));
        Assert.Equal(0, await CountProjectionProcessorsAsync(processorId));
    }

    [Fact]
    public async Task ConcurrentInitialCommitForSameProcessorSerializesToAdvanceThenReconcile()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId($"projection-{Guid.NewGuid():N}");
        var commit = CreateEmptyCommit(processorId, expected: null, source.FirstCheckpoint);
        var modes = new ConcurrentBag<SqlServerOperationalMetricProjectionCommitMode>();
        var first = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var second = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);

        await Task.WhenAll(
            first.ExecuteAsync(
                commit,
                (context, _) =>
                {
                    modes.Add(context.Mode);
                    return Task.CompletedTask;
                },
                CancellationToken.None),
            second.ExecuteAsync(
                commit,
                (context, _) =>
                {
                    modes.Add(context.Mode);
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal(2, modes.Count);
        Assert.Contains(SqlServerOperationalMetricProjectionCommitMode.Advance, modes);
        Assert.Contains(SqlServerOperationalMetricProjectionCommitMode.ReconcileProposed, modes);
        Assert.Equal(1, await CountProjectionProcessorsAsync(processorId));
    }

    [Fact]
    public void StringOrderKeyV2MatchesFrozenKnownVectors()
    {
        Assert.Equal("00", Convert.ToHexString(StringOrderKeyV2Codec.Encode(string.Empty)));
        Assert.Equal("01004100", Convert.ToHexString(StringOrderKeyV2Codec.Encode("A")));
        Assert.Equal("01007801002000", Convert.ToHexString(StringOrderKeyV2Codec.Encode("x ")));
        Assert.Equal("01007801000000", Convert.ToHexString(StringOrderKeyV2Codec.Encode("x\0")));
        Assert.Equal("01D83D01DE0000", Convert.ToHexString(StringOrderKeyV2Codec.Encode("😀")));
    }

    private static Task NoOpBody(
        SqlServerOperationalMetricProjectionCommitContext _,
        CancellationToken __) => Task.CompletedTask;

    private static OperationalMetricProjectionCommit CreateEmptyCommit(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricProjectionCheckpoint? expected,
        MetricAggregationCheckpoint sourceRevision)
    {
        var proposed = new OperationalMetricProjectionCheckpoint(processorId, sourceRevision);
        return new OperationalMetricProjectionCommit(processorId, expected, proposed, []);
    }

    private async Task<SourceFixture> CreateSourceAsync(bool includeThird = false)
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var firstFact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"projection-source-{Guid.NewGuid():N}", 0),
            CancellationToken.None);
        var secondFact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"projection-source-{Guid.NewGuid():N}", 1),
            CancellationToken.None);
        PositionedMetricInputFact? thirdFact = null;
        if (includeThird)
        {
            thirdFact = await inputStore.AppendAsync(
                CreateAppend(machineId, $"projection-source-{Guid.NewGuid():N}", 2),
                CancellationToken.None);
        }

        var aggregationProcessorId = new MetricAggregationProcessorId($"projection-source-{Guid.NewGuid():N}");
        var latest = thirdFact ?? secondFact;
        var durableSourceCheckpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            firstFact.StreamId,
            latest.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                expectedCheckpoint: null,
                durableSourceCheckpoint,
                []),
            CancellationToken.None);

        return new SourceFixture(
            new MetricAggregationCheckpoint(aggregationProcessorId, firstFact.StreamId, firstFact.Position),
            new MetricAggregationCheckpoint(aggregationProcessorId, firstFact.StreamId, secondFact.Position),
            thirdFact is null
                ? null
                : new MetricAggregationCheckpoint(aggregationProcessorId, firstFact.StreamId, thirdFact.Position));
    }

    private static DurableMetricInputAppend CreateAppend(
        MachineId machineId,
        string factId,
        int minute)
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
            new ShiftOccurrenceId(
                siteId,
                scheduleId,
                shiftId,
                occurrenceStart,
                occurrenceStart.AddHours(8)),
            new ProductionDayId(siteId, DateOnly.FromDateTime(occurrenceStart.UtcDateTime)));
    }

    private async Task<int> CountProjectionProcessorsAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.OperationalMetricProjectionProcessor " +
            "WHERE ProcessorKeyBinary = @ProcessorKeyBinary;";
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
            .Value = StringOrderKeyV2Codec.Encode(processorId.Value);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private async Task AssertProcessorKeyV2Async(
        OperationalMetricProjectionProcessorId processorId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ProcessorKeyBinary FROM dbo.OperationalMetricProjectionProcessor " +
            "WHERE ProcessorKeyBinary = @ProcessorKeyBinary;";
        var expected = StringOrderKeyV2Codec.Encode(processorId.Value);
        command.Parameters.Add("@ProcessorKeyBinary", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength)
            .Value = expected;
        var actual = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        Assert.Equal(expected, actual);
    }

    private static async Task<MetricInputPosition?> ReadCheckpointPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Position FROM dbo.OperationalMetricProjectionCheckpoint " +
            "WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull
            ? null
            : new MetricInputPosition(SqlServerUInt64.Materialize((decimal)result));
    }

    private sealed record SourceFixture(
        MetricAggregationCheckpoint FirstCheckpoint,
        MetricAggregationCheckpoint SecondCheckpoint,
        MetricAggregationCheckpoint? ThirdCheckpoint);
}
