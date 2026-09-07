using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
[Trait("Category", "FC030ProjectionExactSlotDiagnostics")]
public sealed class SqlServerOperationalMetricProjectionExactSlotBoundaryDiagnosticsIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string LogicalHashIndex = "UQ_OperationalMetricProjection_LogicalHash";
    private readonly SqlServerTestDatabaseFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlServerOperationalMetricProjectionExactSlotBoundaryDiagnosticsIntegrationTests(
        SqlServerTestDatabaseFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task ExistingAKeyVersusExistingBKey()
    {
        var pair = await CreateOrderedProcessorPairAsync();
        var a = await SeedProjectionAsync(pair.A, "existing-a");
        var b = await SeedProjectionAsync(pair.B, "existing-b");

        await RunCaseAsync(
            "EXISTING_A_VS_EXISTING_B",
            pair.A,
            a.Hash,
            pair.B,
            b.Hash);
    }

    [Fact]
    public async Task InternalMissingAHashVersusExistingBKey()
    {
        var pair = await CreateOrderedProcessorPairAsync();
        var boundaries = await SeedAndFindBoundariesAsync(pair.A);
        var b = await SeedProjectionAsync(pair.B, "existing-b");

        await RunCaseAsync(
            "INTERNAL_MISSING_A_VS_EXISTING_B",
            pair.A,
            boundaries.InternalMissing,
            pair.B,
            b.Hash);
    }

    [Fact]
    public async Task BelowFirstMissingAHashVersusExistingBKey()
    {
        var pair = await CreateOrderedProcessorPairAsync();
        var boundaries = await SeedAndFindBoundariesAsync(pair.A);
        var b = await SeedProjectionAsync(pair.B, "existing-b");

        await RunCaseAsync(
            "BELOW_FIRST_A_VS_EXISTING_B",
            pair.A,
            boundaries.BelowFirst,
            pair.B,
            b.Hash);
    }

    [Fact]
    public async Task AboveLastMissingAHashVersusExistingBKey()
    {
        var pair = await CreateOrderedProcessorPairAsync();
        var boundaries = await SeedAndFindBoundariesAsync(pair.A);
        var b = await SeedProjectionAsync(pair.B, "existing-b");

        await RunCaseAsync(
            "ABOVE_LAST_A_VS_EXISTING_B",
            pair.A,
            boundaries.AboveLast,
            pair.B,
            b.Hash);
    }

    [Fact]
    public async Task EmptyProcessorAMissingHashVersusExistingBKey()
    {
        var pair = await CreateOrderedProcessorPairAsync();
        var b = await SeedProjectionAsync(pair.B, "existing-b");
        var missingA = ComputeHash(pair.A.MachineId, "empty-a-missing");

        await RunCaseAsync(
            "EMPTY_A_MISSING_VS_EXISTING_B",
            pair.A,
            missingA,
            pair.B,
            b.Hash);
    }

    [Fact]
    public async Task ReverseOrderingMissingBHashVersusExistingAKey()
    {
        var pair = await CreateOrderedProcessorPairAsync();
        var a = await SeedProjectionAsync(pair.A, "existing-a");
        var boundaries = await SeedAndFindBoundariesAsync(pair.B);

        await RunCaseAsync(
            "REVERSE_B_BELOW_FIRST_VS_EXISTING_A",
            pair.B,
            boundaries.BelowFirst,
            pair.A,
            a.Hash);
    }

    private async Task RunCaseAsync(
        string label,
        ProcessorFixture ownerProcessor,
        byte[] ownerHash,
        ProcessorFixture contenderProcessor,
        byte[] contenderHash)
    {
        await using var owner = _fixture.CreateConnection();
        await owner.OpenAsync();
        await using var ownerTransaction = (SqlTransaction)await owner.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        await AcquireExactSlotAsync(
            owner,
            ownerTransaction,
            ownerProcessor.ProjectionProcessorRowId,
            ownerHash,
            CancellationToken.None);

        var ownerLocks = await ReadLocksAsync(owner.ServerProcessId);
        WriteLocks($"{label}:owner", ownerProcessor, ownerLocks);

        await using var contender = _fixture.CreateConnection();
        await contender.OpenAsync();
        await using var contenderTransaction = (SqlTransaction)await contender.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);

        var contenderTask = AcquireExactSlotAsync(
            contender,
            contenderTransaction,
            contenderProcessor.ProjectionProcessorRowId,
            contenderHash,
            CancellationToken.None);

        var wait = await ObserveContenderAsync(contender.ServerProcessId, contenderTask);
        if (wait is null)
        {
            _output.WriteLine($"EXACT_SLOT_RESULT={label}:INDEPENDENT");
        }
        else
        {
            _output.WriteLine(
                $"EXACT_SLOT_RESULT={label}:BLOCKED;" +
                $"wait_type={wait.WaitType};wait_resource={wait.WaitResource};" +
                $"blocking_session_id={wait.BlockingSessionId};status={wait.Status}");
            var contenderLocks = await ReadLocksAsync(contender.ServerProcessId);
            WriteLocks($"{label}:contender-waiting", contenderProcessor, contenderLocks);
        }

        await ownerTransaction.RollbackAsync(CancellationToken.None);
        await contenderTask.WaitAsync(TimeSpan.FromSeconds(10));
        await contenderTransaction.RollbackAsync(CancellationToken.None);

        await AssertExactSlotAcquirableAsync(
            ownerProcessor.ProjectionProcessorRowId,
            ownerHash);
    }

    private async Task<WaitSnapshot?> ObserveContenderAsync(int sessionId, Task contenderTask)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (contenderTask.IsCompleted)
            {
                await contenderTask;
                return null;
            }

            var wait = await ReadWaitAsync(sessionId);
            if (wait is not null && wait.BlockingSessionId != 0)
            {
                return wait;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            "Exact-slot diagnostic contender neither completed nor exposed a blocking request within five seconds.");
    }

    private static async Task AcquireExactSlotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        byte[] hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT OperationalMetricProjectionRowId, EvaluationKeyHash
            FROM dbo.OperationalMetricProjection WITH
                (UPDLOCK, HOLDLOCK, INDEX({LogicalHashIndex}))
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId
              AND EvaluationKeyHash = @Hash;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value = hash;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
        }
    }

    private async Task<WaitSnapshot?> ReadWaitAsync(int sessionId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(status, N''),
                COALESCE(wait_type, N''),
                COALESCE(wait_resource, N''),
                blocking_session_id
            FROM sys.dm_exec_requests
            WHERE session_id = @SessionId;
            """;
        command.Parameters.Add("@SessionId", SqlDbType.Int).Value = sessionId;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            return null;
        }

        return new WaitSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt16(3));
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
                l.request_status,
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
        _output.WriteLine(
            $"LOCK_SNAPSHOT={label};ProcessorRowId={processor.ProjectionProcessorRowId}");
        foreach (var item in locks)
        {
            _output.WriteLine(
                $"{item.ResourceType}|{item.RequestMode}|{item.RequestStatus}|" +
                $"{item.IndexName}|{item.AssociatedEntityId}|{item.ResourceDescription}");
        }
    }

    private async Task AssertExactSlotAcquirableAsync(long processorRowId, byte[] hash)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        await AcquireExactSlotAsync(
            connection,
            transaction,
            processorRowId,
            hash,
            CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private async Task<OrderedProcessorPair> CreateOrderedProcessorPairAsync()
    {
        var first = await CreateProcessorAsync();
        var second = await CreateProcessorAsync();
        Assert.True(first.ProjectionProcessorRowId < second.ProjectionProcessorRowId);
        return new OrderedProcessorPair(first, second);
    }

    private async Task<ProcessorFixture> CreateProcessorAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"projection-exact-slot-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"projection-exact-slot-{Guid.NewGuid():N}");
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
            $"projection-exact-slot-{Guid.NewGuid():N}");
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

    private async Task<HashFixture> SeedProjectionAsync(ProcessorFixture processor, string metricKey)
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
                @ProcessorRowId, 1, @Hash, @Binary, @MachineId, 1,
                @SiteId, @SiteOrderKey, @ScheduleId, @ScheduleOrderKey,
                @ShiftId, @ShiftOrderKey, @StartsAtUtc, @EndsAtUtc, NULL,
                0, NULL, NULL, 0, NULL, NULL, 0, NULL, NULL, 0, NULL, NULL,
                @MetricKey, @MetricOrderKey, N'1', @VersionOrderKey,
                0, N'0.5', N'ratio', NULL, NULL, @SourceRevisionPosition
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
        return new HashFixture(metricKey, hash);
    }

    private async Task<BoundaryHashes> SeedAndFindBoundariesAsync(ProcessorFixture processor)
    {
        var candidates = Enumerable.Range(0, 256)
            .Select(index => new HashFixture(
                $"boundary-{index:D3}",
                ComputeHash(processor.MachineId, $"boundary-{index:D3}")))
            .OrderBy(static item => item.Hash, ByteArrayComparer.Instance)
            .ToArray();

        var first = candidates[64];
        var second = candidates[192];
        await SeedProjectionAsync(processor, first.MetricKey);
        await SeedProjectionAsync(processor, second.MetricKey);

        var below = candidates.First(item =>
            ByteArrayComparer.Instance.Compare(item.Hash, first.Hash) < 0);
        var internalMissing = candidates.First(item =>
            ByteArrayComparer.Instance.Compare(item.Hash, first.Hash) > 0 &&
            ByteArrayComparer.Instance.Compare(item.Hash, second.Hash) < 0);
        var above = candidates.Last(item =>
            ByteArrayComparer.Instance.Compare(item.Hash, second.Hash) > 0);

        return new BoundaryHashes(below.Hash, internalMissing.Hash, above.Hash);
    }

    private static byte[] ComputeHash(MachineId machineId, string metricKey) =>
        OperationalMetricEvaluationKeyV1Codec.ComputeHash(
            OperationalMetricEvaluationKeyV1Codec.Encode(
                CreateShiftKey(machineId, metricKey)));

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

    private sealed record OrderedProcessorPair(ProcessorFixture A, ProcessorFixture B);

    private sealed record HashFixture(string MetricKey, byte[] Hash);

    private sealed record BoundaryHashes(
        byte[] BelowFirst,
        byte[] InternalMissing,
        byte[] AboveLast);

    private sealed record LockSnapshot(
        string ResourceType,
        string RequestMode,
        string RequestStatus,
        string IndexName,
        string ResourceDescription,
        long AssociatedEntityId);

    private sealed record WaitSnapshot(
        string Status,
        string WaitType,
        string WaitResource,
        short BlockingSessionId);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y);
        }
    }
}