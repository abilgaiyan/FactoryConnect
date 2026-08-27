using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerMetricAggregationStore : IMetricAggregationStore
{
    private const string DecimalFormat = "G29";
    private readonly string _connectionString;

    public SqlServerMetricAggregationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask<MetricAggregationCheckpoint?> ReadCheckpointAsync(
        MetricAggregationProcessorId processorId,
        MetricInputStreamId streamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var processor = await FindProcessorAsync(
            connection,
            transaction: null,
            processorId,
            cancellationToken);
        if (processor is null)
        {
            return null;
        }

        var requestedStreamRowId = await FindStreamAsync(
            connection,
            transaction: null,
            streamId,
            cancellationToken);
        if (requestedStreamRowId is null ||
            requestedStreamRowId.Value != processor.Value.StreamRowId)
        {
            throw new InvalidOperationException(
                "Aggregation processor checkpoint belongs to a different metric input stream.");
        }

        var position = await ReadCheckpointPositionAsync(
            connection,
            transaction: null,
            processor.Value.RowId,
            cancellationToken);

        return position is null
            ? null
            : new MetricAggregationCheckpoint(
                processorId,
                streamId,
                position);
    }

    public async ValueTask<MetricAggregateValue?> ReadShiftAggregateAsync(
        MetricAggregationProcessorId processorId,
        ShiftMetricAggregateKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        return await ReadAggregateAsync(
            processorId,
            SqlServerMetricAggregateKeyCodec.Encode(key),
            "dbo.ShiftMetricAggregate",
            cancellationToken);
    }

    public async ValueTask<MetricAggregateValue?> ReadProductionDayAggregateAsync(
        MetricAggregationProcessorId processorId,
        ProductionDayMetricAggregateKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        return await ReadAggregateAsync(
            processorId,
            SqlServerMetricAggregateKeyCodec.Encode(key),
            "dbo.ProductionDayMetricAggregate",
            cancellationToken);
    }

    public async ValueTask CommitAsync(
        MetricAggregationCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var sqlTransaction = (SqlTransaction)transaction;

        try
        {
            var streamRowId = await FindStreamAsync(
                connection,
                sqlTransaction,
                commit.ProposedCheckpoint.StreamId,
                cancellationToken,
                lockForUpdate: true)
                ?? throw new InvalidOperationException(
                    "Metric aggregation stream does not exist in durable persistence.");

            var processorRowId = await GetOrCreateProcessorAsync(
                connection,
                sqlTransaction,
                commit.ProcessorId,
                streamRowId,
                cancellationToken);

            var currentPosition = await ReadCheckpointPositionAsync(
                connection,
                sqlTransaction,
                processorRowId,
                cancellationToken,
                lockForUpdate: true);
            ValidateExpectedCheckpoint(commit, currentPosition);

            var stagedInputs = await StageNewInputsAsync(
                connection,
                sqlTransaction,
                processorRowId,
                streamRowId,
                commit,
                cancellationToken);

            var contributions = Aggregate(stagedInputs.Select(static item => item.Input));

            foreach (var contribution in contributions.Shift)
            {
                await MergeShiftAggregateAsync(
                    connection,
                    sqlTransaction,
                    processorRowId,
                    contribution.Key,
                    contribution.Value,
                    cancellationToken);
            }

            foreach (var contribution in contributions.ProductionDay)
            {
                await MergeProductionDayAggregateAsync(
                    connection,
                    sqlTransaction,
                    processorRowId,
                    contribution.Key,
                    contribution.Value,
                    cancellationToken);
            }

            foreach (var staged in stagedInputs)
            {
                await InsertContributionAsync(
                    connection,
                    sqlTransaction,
                    processorRowId,
                    streamRowId,
                    staged.FactRowId,
                    staged.Input.Position,
                    cancellationToken);
            }

            await WriteCheckpointAsync(
                connection,
                sqlTransaction,
                processorRowId,
                currentPosition,
                commit.ProposedCheckpoint.Position,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async ValueTask<MetricAggregateValue?> ReadAggregateAsync(
        MetricAggregationProcessorId processorId,
        byte[] canonicalKey,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var processor = await FindProcessorAsync(
            connection,
            transaction: null,
            processorId,
            cancellationToken);
        if (processor is null)
        {
            return null;
        }

        var persisted = await ReadAggregateRowAsync(
            connection,
            transaction: null,
            processor.Value.RowId,
            canonicalKey,
            tableName,
            cancellationToken);
        return persisted?.Value;
    }

    private static void ValidateExpectedCheckpoint(
        MetricAggregationCommit commit,
        MetricInputPosition? currentPosition)
    {
        var expected = commit.ExpectedCheckpoint?.Position;
        if (expected is null)
        {
            if (currentPosition is not null)
            {
                throw new InvalidOperationException("Metric aggregation checkpoint conflict.");
            }

            return;
        }

        if (currentPosition is null || currentPosition != expected)
        {
            throw new InvalidOperationException("Metric aggregation checkpoint conflict.");
        }
    }

    private static async Task<List<StagedInput>> StageNewInputsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        long streamRowId,
        MetricAggregationCommit commit,
        CancellationToken cancellationToken)
    {
        var staged = new List<StagedInput>();
        var seenFacts = new HashSet<MetricInputFactId>();
        var seenPositions = new HashSet<MetricInputPosition>();

        foreach (var input in commit.Inputs)
        {
            if (input.Position > commit.ProposedCheckpoint.Position)
            {
                throw new InvalidOperationException(
                    "Metric input position cannot exceed the proposed aggregation checkpoint.");
            }

            if (!seenFacts.Add(input.Fact.Id))
            {
                throw new InvalidOperationException(
                    "Aggregation commit contains a duplicate metric input fact identity.");
            }

            if (!seenPositions.Add(input.Position))
            {
                throw new InvalidOperationException(
                    "Aggregation commit contains a duplicate metric input position.");
            }

            var factRowId = await FindExactDurableFactRowIdAsync(
                connection,
                transaction,
                streamRowId,
                input,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "Aggregation input does not match the durable positioned metric input.");

            var existingContributionPosition = await ReadContributionPositionAsync(
                connection,
                transaction,
                processorRowId,
                factRowId,
                cancellationToken);
            if (existingContributionPosition is not null)
            {
                if (existingContributionPosition != input.Position)
                {
                    throw new InvalidOperationException(
                        "Metric input fact identity was replayed at a conflicting position.");
                }

                continue;
            }

            if (commit.ExpectedCheckpoint is not null &&
                input.Position <= commit.ExpectedCheckpoint.Position)
            {
                throw new InvalidOperationException(
                    "New metric input position must be after the expected aggregation checkpoint.");
            }

            var existingFactAtPosition = await ReadContributionFactRowIdAtPositionAsync(
                connection,
                transaction,
                processorRowId,
                input.Position,
                cancellationToken);
            if (existingFactAtPosition is not null && existingFactAtPosition.Value != factRowId)
            {
                throw new InvalidOperationException(
                    "Metric input position was reused for a different fact identity.");
            }

            staged.Add(new StagedInput(input, factRowId));
        }

        return staged;
    }

    private static async Task<long?> FindExactDurableFactRowIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long streamRowId,
        PositionedMetricInputFact input,
        CancellationToken cancellationToken)
    {
        var fact = input.Fact;
        var occurrence = input.ShiftOccurrenceId;
        var productionDay = input.ProductionDayId;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT MetricInputFactRowId FROM dbo.MetricInputFact WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE MetricInputStreamRowId = @StreamRowId AND Position = @Position " +
            "AND FactIdBinary = @FactIdBinary AND FactId = @FactId " +
            "AND MetricInputKey = @MetricInputKey AND MetricValue = @MetricValue AND Unit = @Unit " +
            "AND StartsAtUtc = @StartsAtUtc AND EndsAtUtc = @EndsAtUtc " +
            "AND CompanyId = @CompanyId AND SiteId = @SiteId AND MachineId = @MachineId " +
            "AND ShiftId = @ShiftId AND ShiftScheduleAssignmentId = @ScheduleId " +
            "AND ((ProductionLineId = @ProductionLineId) OR (ProductionLineId IS NULL AND @ProductionLineId IS NULL)) " +
            "AND ((ProductionContextAssignmentId = @ContextId) OR (ProductionContextAssignmentId IS NULL AND @ContextId IS NULL)) " +
            "AND ((ProductionOrderId = @OrderId) OR (ProductionOrderId IS NULL AND @OrderId IS NULL)) " +
            "AND ((OperationId = @OperationId) OR (OperationId IS NULL AND @OperationId IS NULL)) " +
            "AND ((PartId = @PartId) OR (PartId IS NULL AND @PartId IS NULL)) " +
            "AND ((OperatorId = @OperatorId) OR (OperatorId IS NULL AND @OperatorId IS NULL)) " +
            "AND ((IsPlannedProductionTime = @IsPlanned) OR (IsPlannedProductionTime IS NULL AND @IsPlanned IS NULL)) " +
            "AND ((PlannedProductionScheduleAssignmentId = @PlannedAssignmentId) OR (PlannedProductionScheduleAssignmentId IS NULL AND @PlannedAssignmentId IS NULL)) " +
            "AND ((SourceContextualizedActivityIntervalId = @SourceContextId) OR (SourceContextualizedActivityIntervalId IS NULL AND @SourceContextId IS NULL)) " +
            "AND ((SourceEligibilityIntervalId = @SourceEligibilityId) OR (SourceEligibilityIntervalId IS NULL AND @SourceEligibilityId IS NULL)) " +
            "AND ((SourceQuantityEvidenceId = @SourceQuantityId) OR (SourceQuantityEvidenceId IS NULL AND @SourceQuantityId IS NULL)) " +
            "AND OccurrenceSiteId = @OccurrenceSiteId " +
            "AND OccurrenceShiftScheduleAssignmentId = @OccurrenceScheduleId " +
            "AND OccurrenceShiftId = @OccurrenceShiftId " +
            "AND OccurrenceStartsAtUtc = @OccurrenceStartsAtUtc " +
            "AND OccurrenceEndsAtUtc = @OccurrenceEndsAtUtc " +
            "AND ProductionDaySiteId = @ProductionDaySiteId " +
            "AND ProductionBusinessDate = @ProductionBusinessDate;";

        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", input.Position.Value));
        command.Parameters.Add("@FactIdBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(fact.Id.Value);
        AddString(command, "@FactId", fact.Id.Value, 256);
        AddString(command, "@MetricInputKey", fact.Key, 256);
        AddString(command, "@MetricValue", SerializeDecimal(fact.Value), 64);
        AddString(command, "@Unit", fact.Unit, 128);
        command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value = fact.StartsAtUtc;
        command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value = fact.EndsAtUtc;
        AddString(command, "@CompanyId", fact.CompanyId.Value, 256);
        AddString(command, "@SiteId", fact.SiteId.Value, 256);
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = fact.MachineId.Value;
        AddString(command, "@ShiftId", fact.ShiftId.Value, 256);
        AddString(command, "@ScheduleId", fact.ShiftScheduleAssignmentId!.Value.Value, 256);
        AddNullableString(command, "@ProductionLineId", fact.ProductionLineId?.Value, 256);
        AddNullableString(command, "@ContextId", fact.ProductionContextAssignmentId?.Value, 256);
        AddNullableString(command, "@OrderId", fact.ProductionOrderId?.Value, 256);
        AddNullableString(command, "@OperationId", fact.OperationId?.Value, 256);
        AddNullableString(command, "@PartId", fact.PartId?.Value, 256);
        AddNullableString(command, "@OperatorId", fact.OperatorId?.Value, 256);
        command.Parameters.Add("@IsPlanned", SqlDbType.Bit).Value =
            fact.IsPlannedProductionTime is null ? DBNull.Value : fact.IsPlannedProductionTime.Value;
        AddNullableString(
            command,
            "@PlannedAssignmentId",
            fact.PlannedProductionScheduleAssignmentId?.Value,
            256);
        AddNullableString(
            command,
            "@SourceContextId",
            fact.SourceContextualizedActivityIntervalId?.Value,
            256);
        AddNullableString(
            command,
            "@SourceEligibilityId",
            fact.SourceEligibilityIntervalId?.Value,
            256);
        AddNullableString(
            command,
            "@SourceQuantityId",
            fact.SourceQuantityEvidenceId?.Value,
            256);
        AddString(command, "@OccurrenceSiteId", occurrence.SiteId.Value, 256);
        AddString(
            command,
            "@OccurrenceScheduleId",
            occurrence.ShiftScheduleAssignmentId.Value,
            256);
        AddString(command, "@OccurrenceShiftId", occurrence.ShiftId.Value, 256);
        command.Parameters.Add("@OccurrenceStartsAtUtc", SqlDbType.DateTimeOffset).Value =
            occurrence.StartsAtUtc;
        command.Parameters.Add("@OccurrenceEndsAtUtc", SqlDbType.DateTimeOffset).Value =
            occurrence.EndsAtUtc;
        AddString(command, "@ProductionDaySiteId", productionDay.SiteId.Value, 256);
        command.Parameters.Add("@ProductionBusinessDate", SqlDbType.Date).Value =
            productionDay.BusinessDate.ToDateTime(TimeOnly.MinValue);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static ContributionSet Aggregate(IEnumerable<PositionedMetricInputFact> inputs)
    {
        var shift = new Dictionary<ShiftMetricAggregateKey, MutableAggregate>();
        var productionDay = new Dictionary<ProductionDayMetricAggregateKey, MutableAggregate>();

        foreach (var input in inputs.OrderBy(static item => item.Position.Value))
        {
            var shiftKey = new ShiftMetricAggregateKey(
                input.Fact.MachineId,
                input.ShiftOccurrenceId,
                input.Fact.Key);
            AddContribution(shift, shiftKey, input);

            var dayKey = new ProductionDayMetricAggregateKey(
                input.Fact.MachineId,
                input.ProductionDayId,
                input.Fact.Key);
            AddContribution(productionDay, dayKey, input);
        }

        return new ContributionSet(shift, productionDay);
    }

    private static void AddContribution<TKey>(
        Dictionary<TKey, MutableAggregate> aggregates,
        TKey key,
        PositionedMetricInputFact input)
        where TKey : notnull
    {
        if (!aggregates.TryGetValue(key, out var current))
        {
            aggregates.Add(
                key,
                new MutableAggregate(
                    input.Fact.Value,
                    input.Fact.Unit,
                    1,
                    input.Fact.StartsAtUtc,
                    input.Fact.EndsAtUtc));
            return;
        }

        if (!string.Equals(current.Unit, input.Fact.Unit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Metric inputs with the same aggregate key must use compatible units.");
        }

        current.Value = checked(current.Value + input.Fact.Value);
        current.InputCount = checked(current.InputCount + 1);
        if (input.Fact.StartsAtUtc < current.FirstInputTimestamp)
        {
            current.FirstInputTimestamp = input.Fact.StartsAtUtc;
        }

        if (input.Fact.EndsAtUtc > current.LastInputTimestamp)
        {
            current.LastInputTimestamp = input.Fact.EndsAtUtc;
        }
    }

    private static async Task MergeShiftAggregateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ShiftMetricAggregateKey key,
        MutableAggregate contribution,
        CancellationToken cancellationToken)
    {
        var canonicalKey = SqlServerMetricAggregateKeyCodec.Encode(key);
        var persisted = await ReadAggregateRowAsync(
            connection,
            transaction,
            processorRowId,
            canonicalKey,
            "dbo.ShiftMetricAggregate",
            cancellationToken,
            lockForUpdate: true);
        var value = Merge(persisted?.Value, contribution.ToValue());

        if (persisted is null)
        {
            await InsertShiftAggregateAsync(
                connection,
                transaction,
                processorRowId,
                key,
                canonicalKey,
                value,
                cancellationToken);
        }
        else
        {
            await UpdateAggregateAsync(
                connection,
                transaction,
                "dbo.ShiftMetricAggregate",
                "ShiftMetricAggregateRowId",
                persisted.Value.RowId,
                value,
                cancellationToken);
        }
    }

    private static async Task MergeProductionDayAggregateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ProductionDayMetricAggregateKey key,
        MutableAggregate contribution,
        CancellationToken cancellationToken)
    {
        var canonicalKey = SqlServerMetricAggregateKeyCodec.Encode(key);
        var persisted = await ReadAggregateRowAsync(
            connection,
            transaction,
            processorRowId,
            canonicalKey,
            "dbo.ProductionDayMetricAggregate",
            cancellationToken,
            lockForUpdate: true);
        var value = Merge(persisted?.Value, contribution.ToValue());

        if (persisted is null)
        {
            await InsertProductionDayAggregateAsync(
                connection,
                transaction,
                processorRowId,
                key,
                canonicalKey,
                value,
                cancellationToken);
        }
        else
        {
            await UpdateAggregateAsync(
                connection,
                transaction,
                "dbo.ProductionDayMetricAggregate",
                "ProductionDayMetricAggregateRowId",
                persisted.Value.RowId,
                value,
                cancellationToken);
        }
    }

    private static MetricAggregateValue Merge(
        MetricAggregateValue? current,
        MetricAggregateValue contribution)
    {
        if (current is null)
        {
            return contribution;
        }

        if (!string.Equals(current.Unit, contribution.Unit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Persisted aggregate and contribution units are incompatible.");
        }

        return new MetricAggregateValue(
            checked(current.Value + contribution.Value),
            current.Unit,
            checked(current.InputCount + contribution.InputCount),
            current.FirstInputTimestamp <= contribution.FirstInputTimestamp
                ? current.FirstInputTimestamp
                : contribution.FirstInputTimestamp,
            current.LastInputTimestamp >= contribution.LastInputTimestamp
                ? current.LastInputTimestamp
                : contribution.LastInputTimestamp);
    }

    private static async Task<AggregateRow?> ReadAggregateRowAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long processorRowId,
        byte[] canonicalKey,
        string tableName,
        CancellationToken cancellationToken,
        bool lockForUpdate = false)
    {
        var rowIdColumn = tableName.EndsWith("ShiftMetricAggregate", StringComparison.Ordinal)
            ? "ShiftMetricAggregateRowId"
            : "ProductionDayMetricAggregateRowId";
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {rowIdColumn}, AggregateKeyBinary, AggregateValue, Unit, InputCount, " +
            $"FirstInputTimestamp, LastInputTimestamp FROM {tableName}{hint} " +
            "WHERE MetricAggregationProcessorRowId = @ProcessorRowId " +
            "AND AggregateKeyHash = @AggregateKeyHash;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@AggregateKeyHash", SqlDbType.Binary, 32).Value =
            SqlServerMetricAggregateKeyCodec.Hash(canonicalKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var persistedKey = (byte[])reader[1];
        if (!persistedKey.AsSpan().SequenceEqual(canonicalKey))
        {
            throw new InvalidOperationException(
                "Metric aggregate identity hash collision detected.");
        }

        return new AggregateRow(
            reader.GetInt64(0),
            new MetricAggregateValue(
                DeserializeDecimal(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetDateTimeOffset(5),
                reader.GetDateTimeOffset(6)));
    }

    private static async Task InsertShiftAggregateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ShiftMetricAggregateKey key,
        byte[] canonicalKey,
        MetricAggregateValue value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO dbo.ShiftMetricAggregate " +
            "(MetricAggregationProcessorRowId, AggregateKeyHash, AggregateKeyBinary, MachineId, " +
            "SiteId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc, " +
            "MetricInputKey, AggregateValue, Unit, InputCount, FirstInputTimestamp, LastInputTimestamp) " +
            "VALUES (@ProcessorRowId, @Hash, @Key, @MachineId, @SiteId, @ScheduleId, @ShiftId, " +
            "@StartsAtUtc, @EndsAtUtc, @MetricInputKey, @Value, @Unit, @InputCount, @First, @Last);";
        AddAggregateIdentityParameters(command, processorRowId, canonicalKey);
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = key.MachineId.Value;
        AddString(command, "@SiteId", key.ShiftOccurrenceId.SiteId.Value, 256);
        AddString(command, "@ScheduleId", key.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value, 256);
        AddString(command, "@ShiftId", key.ShiftOccurrenceId.ShiftId.Value, 256);
        command.Parameters.Add("@StartsAtUtc", SqlDbType.DateTimeOffset).Value = key.ShiftOccurrenceId.StartsAtUtc;
        command.Parameters.Add("@EndsAtUtc", SqlDbType.DateTimeOffset).Value = key.ShiftOccurrenceId.EndsAtUtc;
        AddString(command, "@MetricInputKey", key.MetricInputKey, 256);
        AddAggregateValueParameters(command, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertProductionDayAggregateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ProductionDayMetricAggregateKey key,
        byte[] canonicalKey,
        MetricAggregateValue value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO dbo.ProductionDayMetricAggregate " +
            "(MetricAggregationProcessorRowId, AggregateKeyHash, AggregateKeyBinary, MachineId, " +
            "SiteId, ProductionBusinessDate, MetricInputKey, AggregateValue, Unit, InputCount, " +
            "FirstInputTimestamp, LastInputTimestamp) VALUES (@ProcessorRowId, @Hash, @Key, " +
            "@MachineId, @SiteId, @BusinessDate, @MetricInputKey, @Value, @Unit, @InputCount, @First, @Last);";
        AddAggregateIdentityParameters(command, processorRowId, canonicalKey);
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = key.MachineId.Value;
        AddString(command, "@SiteId", key.ProductionDayId.SiteId.Value, 256);
        command.Parameters.Add("@BusinessDate", SqlDbType.Date).Value =
            key.ProductionDayId.BusinessDate.ToDateTime(TimeOnly.MinValue);
        AddString(command, "@MetricInputKey", key.MetricInputKey, 256);
        AddAggregateValueParameters(command, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAggregateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableName,
        string rowIdColumn,
        long rowId,
        MetricAggregateValue value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"UPDATE {tableName} SET AggregateValue = @Value, Unit = @Unit, InputCount = @InputCount, " +
            $"FirstInputTimestamp = @First, LastInputTimestamp = @Last WHERE {rowIdColumn} = @RowId;";
        command.Parameters.Add("@RowId", SqlDbType.BigInt).Value = rowId;
        AddAggregateValueParameters(command, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertContributionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        long streamRowId,
        long factRowId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO dbo.MetricAggregationContribution " +
            "(MetricAggregationProcessorRowId, MetricInputStreamRowId, MetricInputFactRowId, Position) " +
            "VALUES (@ProcessorRowId, @StreamRowId, @FactRowId, @Position);";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        command.Parameters.Add("@FactRowId", SqlDbType.BigInt).Value = factRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteCheckpointAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition? current,
        MetricInputPosition proposed,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (current is null)
        {
            command.CommandText =
                "INSERT INTO dbo.MetricAggregationCheckpoint " +
                "(MetricAggregationProcessorRowId, Position) VALUES (@ProcessorRowId, @Position);";
        }
        else
        {
            command.CommandText =
                "UPDATE dbo.MetricAggregationCheckpoint SET Position = @Position " +
                "WHERE MetricAggregationProcessorRowId = @ProcessorRowId AND Position = @ExpectedPosition;";
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@ExpectedPosition", current.Value));
        }

        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", proposed.Value));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException("Metric aggregation checkpoint conflict.");
        }
    }

    private static async Task<long> GetOrCreateProcessorAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MetricAggregationProcessorId processorId,
        long streamRowId,
        CancellationToken cancellationToken)
    {
        var existing = await FindProcessorAsync(
            connection,
            transaction,
            processorId,
            cancellationToken,
            lockForUpdate: true);
        if (existing is not null)
        {
            if (existing.Value.StreamRowId != streamRowId)
            {
                throw new InvalidOperationException(
                    "Aggregation processor cannot change metric input streams.");
            }

            return existing.Value.RowId;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO dbo.MetricAggregationProcessor " +
            "(ProcessorKeyBinary, ProcessorKey, MetricInputStreamRowId) " +
            "OUTPUT INSERTED.MetricAggregationProcessorRowId " +
            "VALUES (@KeyBinary, @Key, @StreamRowId);";
        command.Parameters.Add("@KeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(processorId.Value);
        AddString(command, "@Key", processorId.Value, 256);
        command.Parameters.Add("@StreamRowId", SqlDbType.BigInt).Value = streamRowId;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<(long RowId, long StreamRowId)?> FindProcessorAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        MetricAggregationProcessorId processorId,
        CancellationToken cancellationToken,
        bool lockForUpdate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT MetricAggregationProcessorRowId, MetricInputStreamRowId " +
            "FROM dbo.MetricAggregationProcessor" + hint +
            " WHERE ProcessorKeyBinary = @KeyBinary;";
        command.Parameters.Add("@KeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(processorId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<long?> FindStreamAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        MetricInputStreamId streamId,
        CancellationToken cancellationToken,
        bool lockForUpdate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT MetricInputStreamRowId FROM dbo.MetricInputStream" + hint +
            " WHERE MachineId = @MachineId AND StreamKeyBinary = @StreamKeyBinary;";
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            streamId.MachineId.Value;
        command.Parameters.Add("@StreamKeyBinary", SqlDbType.VarBinary, 512).Value =
            OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<MetricInputPosition?> ReadCheckpointPositionAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long processorRowId,
        CancellationToken cancellationToken,
        bool lockForUpdate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT Position FROM dbo.MetricAggregationCheckpoint" + hint +
            " WHERE MetricAggregationProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : new MetricInputPosition(SqlServerUInt64.Materialize((decimal)result));
    }

    private static async Task<MetricInputPosition?> ReadContributionPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        long factRowId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Position FROM dbo.MetricAggregationContribution WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE MetricAggregationProcessorRowId = @ProcessorRowId AND MetricInputFactRowId = @FactRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@FactRowId", SqlDbType.BigInt).Value = factRowId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : new MetricInputPosition(SqlServerUInt64.Materialize((decimal)result));
    }

    private static async Task<long?> ReadContributionFactRowIdAtPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition position,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT MetricInputFactRowId FROM dbo.MetricAggregationContribution WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE MetricAggregationProcessorRowId = @ProcessorRowId AND Position = @Position;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static void AddAggregateIdentityParameters(
        SqlCommand command,
        long processorRowId,
        byte[] canonicalKey)
    {
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add("@Hash", SqlDbType.Binary, 32).Value =
            SqlServerMetricAggregateKeyCodec.Hash(canonicalKey);
        command.Parameters.Add("@Key", SqlDbType.VarBinary, -1).Value = canonicalKey;
    }

    private static void AddAggregateValueParameters(
        SqlCommand command,
        MetricAggregateValue value)
    {
        AddString(command, "@Value", SerializeDecimal(value.Value), 64);
        AddString(command, "@Unit", value.Unit, 128);
        command.Parameters.Add("@InputCount", SqlDbType.BigInt).Value = value.InputCount;
        command.Parameters.Add("@First", SqlDbType.DateTimeOffset).Value = value.FirstInputTimestamp;
        command.Parameters.Add("@Last", SqlDbType.DateTimeOffset).Value = value.LastInputTimestamp;
    }

    private static void AddString(SqlCommand command, string name, string value, int size) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddNullableString(
        SqlCommand command,
        string name,
        string? value,
        int size) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value =
            value is null ? DBNull.Value : value;

    private static string SerializeDecimal(decimal value) =>
        value.ToString(DecimalFormat, CultureInfo.InvariantCulture);

    private static decimal DeserializeDecimal(string value) =>
        decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private sealed class MutableAggregate
    {
        public MutableAggregate(
            decimal value,
            string unit,
            long inputCount,
            DateTimeOffset firstInputTimestamp,
            DateTimeOffset lastInputTimestamp)
        {
            Value = value;
            Unit = unit;
            InputCount = inputCount;
            FirstInputTimestamp = firstInputTimestamp;
            LastInputTimestamp = lastInputTimestamp;
        }

        public decimal Value { get; set; }
        public string Unit { get; }
        public long InputCount { get; set; }
        public DateTimeOffset FirstInputTimestamp { get; set; }
        public DateTimeOffset LastInputTimestamp { get; set; }

        public MetricAggregateValue ToValue() =>
            new(Value, Unit, InputCount, FirstInputTimestamp, LastInputTimestamp);
    }

    private sealed record StagedInput(PositionedMetricInputFact Input, long FactRowId);
    private sealed record AggregateRow(long RowId, MetricAggregateValue Value);
    private sealed record ContributionSet(
        Dictionary<ShiftMetricAggregateKey, MutableAggregate> Shift,
        Dictionary<ProductionDayMetricAggregateKey, MutableAggregate> ProductionDay);
}
