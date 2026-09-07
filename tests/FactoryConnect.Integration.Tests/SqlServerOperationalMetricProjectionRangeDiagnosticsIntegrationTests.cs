using System.Data;
using System.Text;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
[Trait("Category", "FC030ProjectionLockDiagnostics")]
public sealed class SqlServerOperationalMetricProjectionRangeDiagnosticsIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string LogicalHashIndex = "UQ_OperationalMetricProjection_LogicalHash";
    private readonly SqlServerTestDatabaseFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlServerOperationalMetricProjectionRangeDiagnosticsIntegrationTests(
        SqlServerTestDatabaseFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task FrozenProcessorPrefixPlanUsesLogicalHashSeek()
    {
        var processor = await CreateProcessorAsync();
        await SeedProjectionAsync(processor, "a");
        await SeedProjectionAsync(processor, "m");
        await SeedProjectionAsync(processor, "z");

        var planXml = await ReadActualPlanXmlAsync(processor.ProjectionProcessorRowId);
        _output.WriteLine(planXml);

        Assert.Contains(LogicalHashIndex, planXml, StringComparison.Ordinal);
        Assert.Contains("Index Seek", planXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OperationalMetricProjectionProcessorRowId", planXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PopulatedPrefixReportsGrantedKeyRangeLocksWithoutEscalation()
    {
        var before = await CreateProcessorAsync();
        var target = await CreateProcessorAsync();
        var after = await CreateProcessorAsync();
        await SeedProjectionAsync(before, "neighbor-before");
        await SeedProjectionAsync(target, "a");
        await SeedProjectionAsync(target, "m");
        await SeedProjectionAsync(target, "z");
        await SeedProjectionAsync(after, "neighbor-after");

        await using var owner = _fixture.CreateConnection();
        await owner.OpenAsync();
        await using var transaction = (SqlTransaction)await owner.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        await AcquirePrefixAsync(owner, transaction, target.ProjectionProcessorRowId);
        var locks = await ReadLocksAsync(owner.ServerProcessId);
        WriteLocks("populated-prefix", target, locks);

        Assert.Contains(
            locks,
            static item => item.ResourceType == "KEY" &&
                item.RequestStatus == "GRANT" &&
                item.RequestMode.StartsWith("Range", StringComparison.Ordinal));
        Assert.DoesNotContain(
            locks,
            static item => item.RequestStatus == "GRANT" &&
                item.ResourceType is "OBJECT" or "PAGE" &&
                item.RequestMode is "S" or "U" or "X" or "SIX");

        await transaction.RollbackAsync(CancellationToken.None);
        await AssertPrefixAcquirableAsync(target.ProjectionProcessorRowId);
    }

    [Fact]
    public async Task EmptyPrefixReportsPhantomProtectionAndReleasesAfterCommit()
    {
        var before = await CreateProcessorAsync();
        var target = await CreateProcessorAsync();
        var after = await CreateProcessorAsync();
        await SeedProjectionAsync(before, "neighbor-before");
        await SeedProjectionAsync(after, "neighbor-after");

        await using var owner = _fixture.CreateConnection();
        await owner.OpenAsync();
        await using var transaction = (SqlTransaction)await owner.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        await AcquirePrefixAsync(owner, transaction, target.ProjectionProcessorRowId);
        var locks = await ReadLocksAsync(owner.ServerProcessId);
        WriteLocks("empty-prefix", target, locks);

        Assert.Contains(
            locks,
            static item => item.ResourceType == "KEY" &&
                item.RequestStatus == "GRANT" &&
                item.RequestMode.StartsWith("Range", StringComparison.Ordinal));

        await transaction.CommitAsync(CancellationToken.None);
        await AssertPrefixAcquirableAsync(target.ProjectionProcessorRowId);
    }

    [Fact]
    public async Task AdjacentNonemptyPrefixesExposeBoundaryCompatibility()
    {
        var processorA = await CreateProcessorAsync();
        var processorB = await CreateProcessorAsync();
        await SeedProjectionAsync(processorA, "a");
        await SeedProjectionAsync(processorA, "z");
        await SeedProjectionAsync(processorB, "a");
        await SeedProjectionAsync(processorB, "z");

        await using var ownerA = _fixture.CreateConnection();
        await ownerA.OpenAsync();
        await using var transactionA = (SqlTransaction)await ownerA.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        await AcquirePrefixAsync(ownerA, transactionA, processorA.ProjectionProcessorRowId);

        var locksA = await ReadLocksAsync(ownerA.ServerProcessId);
        WriteLocks("processor-A", processorA, locksA);

        await using var ownerB = _fixture.CreateConnection();
        await ownerB.OpenAsync();
        await SetZeroLockTimeoutAsync(ownerB);
        await using var transactionB = (SqlTransaction)await ownerB.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        SqlException? boundaryConflict = null;
        try
        {
            await AcquirePrefixAsync(ownerB, transactionB, processorB.ProjectionProcessorRowId);
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            boundaryConflict = exception;
        }

        var locksB = await ReadLocksAsync(ownerB.ServerProcessId);
        WriteLocks("processor-B", processorB, locksB);

        if (boundaryConflict is null)
        {
            _output.WriteLine("DISTINCT_PROCESSOR_RESULT=INDEPENDENT");
            await transactionB.RollbackAsync(CancellationToken.None);
        }
        else
        {
            _output.WriteLine("DISTINCT_PROCESSOR_RESULT=BLOCKED_1222");
            await transactionB.RollbackAsync(CancellationToken.None);
        }

        await transactionA.RollbackAsync(CancellationToken.None);

        // This diagnostic deliberately records rather than normalizes the result. The
        // production-path conformance test remains the authoritative red/green gate.
        Assert.NotEmpty(locksA);
        Assert.NotEmpty(locksB);
    }

    private async Task<ProcessorFixture> CreateProcessorAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"projection-range-diagnostic-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"projection-range-diagnostic-{Guid.NewGuid():N}");
        var sourceRevision = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                expectedCheckpoint: null,
                sourceRevision,
                []),
            CancellationToken.None);

        var processorId = new OperationalMetricProjectionProcessorId(
            $"projection-range-diagnostic-{Guid.NewGuid():N}");
        var projectionCommit = new OperationalMetricProjectionCommit(
            processorId,
            expectedCheckpoint: null,
            new OperationalMetricProjectionCheckpoint(processorId, sourceRevision),
            []);
        var coordinator = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        await coordinator.ExecuteAsync(
            projectionCommit,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var header = await coordinator.ReadCheckpointHeaderAsync(
            processorId,
            CancellationToken.None);
        Assert.NotNull(header);

        return new ProcessorFixture(
            processorId,
            machineId,
            sourceRevision,
            header.ProjectionProcessorRowId);
    }

    private async Task SeedProjectionAsync(ProcessorFixture processor, string metricKey)
    {
        var key = CreateShiftKey(processor.MachineId, metricKey);
        var binary = OperationalMetricEvaluationKeyV1Codec.Encode(key);
        var hash = OperationalMetricEvaluationKeyV1Codec.ComputeHash(binary);
        var shift = Assert.IsType<OperationalMetricPeriodId.Shift>(key.PeriodId);

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.OperationalMetricProjection
            (
                OperationalMetricProjectionProcessorRowId,
                EvaluationKeyCodecVersion,
                EvaluationKeyHash,
                EvaluationKeyBinary,
                MachineId,
                PeriodKind,
                PeriodSiteId,
                PeriodSiteOrderKey,
                ShiftScheduleAssignmentId,
                ShiftScheduleAssignmentOrderKey,
                ShiftId,
                ShiftOrderKey,
                ShiftStartsAtUtc,
                ShiftEndsAtUtc,
                ProductionBusinessDate,
                ProductionOrderPresent,
                ProductionOrderId,
                ProductionOrderOrderKey,
                OperationPresent,
                OperationId,
                OperationOrderKey,
                PartPresent,
                PartId,
                PartOrderKey,
                OperatorPresent,
                OperatorId,
                OperatorOrderKey,
                MetricKey,
                MetricKeyOrderKey,
                DefinitionVersion,
                DefinitionVersionOrderKey,
                Status,
                MetricValue,
                Unit,
                ReasonCode,
                ReasonOperandName,
                SourceRevisionPosition
            )
            VALUES
            (
                @ProcessorRowId,
                1,
                @Hash,
                @Binary,
                @MachineId,
                1,
                @SiteId,
                @SiteOrderKey,
                @ScheduleId,
                @ScheduleOrderKey,
                @ShiftId,
                @ShiftOrderKey,
                @StartsAtUtc,
                @EndsAtUtc,
                NULL,
                0, NULL, NULL,
                0, NULL, NULL,
                0, NULL, NULL,
                0, NULL, NULL,
                @MetricKey,
                @MetricOrderKey,
                N'1',
                @VersionOrderKey,
                0,
                N'0.5',
                N'ratio',
                NULL,
                NULL,
                @SourceRevisionPosition
            );
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value =
            processor.ProjectionProcessorRowId;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = hash;
        command.Parameters.Add("@Binary", SqlDbType.VarBinary, -1).Value = binary;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = processor.MachineId.Value;
        AddString(command, "@SiteId", shift.ShiftOccurrenceId.SiteId.Value);
        AddOrderKey(command, "@SiteOrderKey", shift.ShiftOccurrenceId.SiteId.Value);
        AddString(command, "@ScheduleId", shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddOrderKey(command, "@ScheduleOrderKey", shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value);
        AddString(command, "@ShiftId", shift.ShiftOccurrenceId.ShiftId.Value);
        AddOrderKey(command, "@ShiftOrderKey", shift.ShiftOccurrenceId.ShiftId.Value);
        command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value = shift.ShiftOccurrenceId.StartsAtUtc;
        command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value = shift.ShiftOccurrenceId.EndsAtUtc;
        AddString(command, "@MetricKey", metricKey);
        AddOrderKey(command, "@MetricOrderKey", metricKey);
        AddOrderKey(command, "@VersionOrderKey", "1");
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@SourceRevisionPosition",
                processor.SourceRevision.Position.Value));

        Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private async Task<string> ReadActualPlanXmlAsync(long processorRowId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SET STATISTICS XML ON;
            SELECT p.*
            FROM dbo.OperationalMetricProjection AS p WITH
                (UPDLOCK, HOLDLOCK, INDEX({LogicalHashIndex}))
            WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY p.EvaluationKeyHash;
            SET STATISTICS XML OFF;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        do
        {
            if (reader.FieldCount == 1 && reader.GetName(0).Contains("Microsoft SQL Server", StringComparison.OrdinalIgnoreCase))
            {
                if (await reader.ReadAsync(CancellationToken.None))
                {
                    return reader.GetString(0);
                }
            }

            while (await reader.ReadAsync(CancellationToken.None))
            {
                if (reader.FieldCount == 1 && !reader.IsDBNull(0))
                {
                    var candidate = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                    if (candidate?.Contains("ShowPlanXML", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return candidate;
                    }
                }
            }
        }
        while (await reader.NextResultAsync(CancellationToken.None));

        throw new InvalidOperationException("SQL Server did not return STATISTICS XML for the frozen prefix query.");
    }

    private static async Task AcquirePrefixAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT EvaluationKeyHash
            FROM dbo.OperationalMetricProjection WITH
                (UPDLOCK, HOLDLOCK, INDEX({LogicalHashIndex}))
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
            ORDER BY EvaluationKeyHash;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
        }
    }

    private async Task<IReadOnlyList<LockSnapshot>> ReadLocksAsync(int sessionId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                l.resource_type,
                l.request_mode,
                l.request_status,
                COALESCE(i.name, N''),
                COALESCE(l.resource_description, N''),
                l.resource_associated_entity_id
            FROM sys.dm_tran_locks AS l
            LEFT JOIN sys.partitions AS p
              ON p.hobt_id = l.resource_associated_entity_id
            LEFT JOIN sys.indexes AS i
              ON i.object_id = p.object_id
             AND i.index_id = p.index_id
            WHERE l.request_session_id = @SessionId
              AND l.resource_database_id = DB_ID()
            ORDER BY
                l.resource_type,
                l.request_mode,
                l.resource_associated_entity_id,
                l.resource_description;
            """;
        command.Parameters.Add("@SessionId", SqlDbType.Int).Value = sessionId;

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var result = new List<LockSnapshot>();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            result.Add(new LockSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        }

        return result;
    }

    private void WriteLocks(
        string label,
        ProcessorFixture processor,
        IReadOnlyList<LockSnapshot> locks)
    {
        _output.WriteLine($"LOCK_SNAPSHOT={label}; ProcessorRowId={processor.ProjectionProcessorRowId}");
        foreach (var item in locks)
        {
            _output.WriteLine(
                $"{item.ResourceType}|{item.RequestMode}|{item.RequestStatus}|" +
                $"{item.IndexName}|{item.AssociatedEntityId}|{item.ResourceDescription}");
        }
    }

    private async Task AssertPrefixAcquirableAsync(long processorRowId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await SetZeroLockTimeoutAsync(connection);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        await AcquirePrefixAsync(connection, transaction, processorRowId);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private static async Task SetZeroLockTimeoutAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SET LOCK_TIMEOUT 0;";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static OperationalMetricEvaluationKey CreateShiftKey(MachineId machineId, string metricKey) =>
        new(
            machineId,
            new OperationalMetricPeriodId.Shift(
                new ShiftOccurrenceId(
                    new SiteId("SITE-1"),
                    new ShiftScheduleAssignmentId("SCHEDULE-A"),
                    new ShiftId("SHIFT-A"),
                    new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero))),
            new OperationalMetricDefinitionId(metricKey, "1"),
            OperationalMetricEvaluationContextKey.Unpartitioned);

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = 1m,
            Unit = "seconds",
            StartsAtUtc = occurrenceStart,
            EndsAtUtc = occurrenceStart.AddMinutes(1),
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

    private static void AddString(SqlCommand command, string name, string value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, 256).Value = value;

    private static void AddOrderKey(SqlCommand command, string name, string value) =>
        command.Parameters.Add(name, SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(value);

    private sealed record ProcessorFixture(
        OperationalMetricProjectionProcessorId ProcessorId,
        MachineId MachineId,
        MetricAggregationCheckpoint SourceRevision,
        long ProjectionProcessorRowId);

    private sealed record LockSnapshot(
        string ResourceType,
        string RequestMode,
        string RequestStatus,
        string IndexName,
        string ResourceDescription,
        long AssociatedEntityId);
}
