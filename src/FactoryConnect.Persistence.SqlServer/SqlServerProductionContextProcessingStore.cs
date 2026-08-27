using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerProductionContextProcessingStore :
    IProductionContextProcessingStore
{
    private readonly string _connectionString;

    public SqlServerProductionContextProcessingStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var processorRowId = await FindProcessorRowIdAsync(
            connection,
            transaction: null,
            processorId,
            streamId,
            lockForUpdate: false,
            cancellationToken);
        if (processorRowId is null)
        {
            return null;
        }

        var position = await ReadCheckpointPositionAsync(
            connection,
            transaction: null,
            processorRowId.Value,
            lockForUpdate: false,
            cancellationToken);
        return position is null
            ? null
            : new ObservationProcessingCheckpoint(
                processorId,
                streamId,
                new ObservationPosition(position.Value));
    }

    public async Task CommitAsync(
        ProductionContextProcessingCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(commit.NextCheckpoint);
        ValidateCommitShape(commit);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var sqlTransaction = (SqlTransaction)transaction;

        try
        {
            var next = commit.NextCheckpoint;
            var processorRowId = await GetOrCreateProcessorRowIdAsync(
                connection,
                sqlTransaction,
                next.ProcessorId,
                next.StreamId,
                cancellationToken);
            var current = await ReadCheckpointPositionAsync(
                connection,
                sqlTransaction,
                processorRowId,
                lockForUpdate: true,
                cancellationToken);
            ValidateExpectedCheckpoint(commit, current);

            foreach (var append in commit.MetricInputs)
            {
                await SqlServerMetricInputTransactionWriter.AppendAsync(
                    connection,
                    sqlTransaction,
                    append,
                    cancellationToken);
            }

            foreach (var item in commit.ContextualizedActivity)
            {
                await PersistOutputAsync(
                    connection,
                    sqlTransaction,
                    "dbo.ContextualizedActivityOutput",
                    item.Id.Value,
                    JsonSerializer.Serialize(item),
                    "contextualized activity",
                    cancellationToken);
            }

            foreach (var item in commit.EligibilityIntervals)
            {
                await PersistOutputAsync(
                    connection,
                    sqlTransaction,
                    "dbo.ProductionTimeEligibilityOutput",
                    item.Id.Value,
                    JsonSerializer.Serialize(item),
                    "production time eligibility",
                    cancellationToken);
            }

            await WriteCheckpointAsync(
                connection,
                sqlTransaction,
                processorRowId,
                current,
                next.Position.Value,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidateCommitShape(ProductionContextProcessingCommit commit)
    {
        var next = commit.NextCheckpoint;
        if (commit.ExpectedCheckpoint is not null &&
            (commit.ExpectedCheckpoint.ProcessorId != next.ProcessorId ||
             commit.ExpectedCheckpoint.StreamId != next.StreamId))
        {
            throw new ArgumentException(
                "Expected production-context checkpoint must belong to the same processor and stream as the next checkpoint.",
                nameof(commit));
        }

        ValidateUnique(
            commit.ContextualizedActivity.Select(static item => item.Id),
            "contextualized activity");
        ValidateUnique(
            commit.EligibilityIntervals.Select(static item => item.Id),
            "production time eligibility");
        ValidateUnique(
            commit.MetricFacts.Select(static item => item.Id),
            "metric fact");
        ValidateUnique(
            commit.MetricInputs.Select(static item => item.Fact.Id),
            "metric input");
        ValidateMetricInputEquivalence(commit.MetricFacts, commit.MetricInputs);
    }

    private static void ValidateExpectedCheckpoint(
        ProductionContextProcessingCommit commit,
        ulong? current)
    {
        var expected = commit.ExpectedCheckpoint?.Position.Value;
        if (expected is null)
        {
            if (current is not null)
            {
                throw new InvalidOperationException(
                    "Production context processing checkpoint conflict.");
            }
        }
        else if (current is null || current.Value != expected.Value)
        {
            throw new InvalidOperationException(
                "Production context processing checkpoint conflict.");
        }

        if (commit.NextCheckpoint.Position.Value <= (current ?? 0UL))
        {
            throw new InvalidOperationException(
                "Production context processing checkpoint must advance.");
        }
    }

    private static async Task<long> GetOrCreateProcessorRowIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken)
    {
        var existing = await FindProcessorRowIdAsync(
            connection,
            transaction,
            processorId,
            streamId,
            lockForUpdate: true,
            cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var processorBinary = OrdinalStringKeyCodec.Encode(processorId.Value);
        var streamBinary = OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO dbo.ProductionContextProcessor " +
            "(ProcessorKeyHash, ProcessorKeyBinary, ProcessorKey, MachineId, " +
            "ObservationStreamKeyHash, ObservationStreamKeyBinary, ObservationStreamKey) " +
            "OUTPUT INSERTED.ProductionContextProcessorRowId " +
            "VALUES (@ProcessorHash, @ProcessorBinary, @ProcessorKey, @MachineId, " +
            "@StreamHash, @StreamBinary, @StreamKey);";
        command.Parameters.Add("@ProcessorHash", SqlDbType.Binary, 32).Value = SHA256.HashData(processorBinary);
        command.Parameters.Add("@ProcessorBinary", SqlDbType.VarBinary, 512).Value = processorBinary;
        AddString(command, "@ProcessorKey", processorId.Value, 256);
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = streamId.MachineId.Value;
        command.Parameters.Add("@StreamHash", SqlDbType.Binary, 32).Value = SHA256.HashData(streamBinary);
        command.Parameters.Add("@StreamBinary", SqlDbType.VarBinary, 512).Value = streamBinary;
        AddString(command, "@StreamKey", streamId.StreamKey, 256);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<long?> FindProcessorRowIdAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var processorBinary = OrdinalStringKeyCodec.Encode(processorId.Value);
        var streamBinary = OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT ProductionContextProcessorRowId, ProcessorKeyBinary, ObservationStreamKeyBinary " +
            "FROM dbo.ProductionContextProcessor" + hint + " " +
            "WHERE ProcessorKeyHash = @ProcessorHash AND MachineId = @MachineId " +
            "AND ObservationStreamKeyHash = @StreamHash;";
        command.Parameters.Add("@ProcessorHash", SqlDbType.Binary, 32).Value = SHA256.HashData(processorBinary);
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = streamId.MachineId.Value;
        command.Parameters.Add("@StreamHash", SqlDbType.Binary, 32).Value = SHA256.HashData(streamBinary);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!((byte[])reader[1]).AsSpan().SequenceEqual(processorBinary) ||
            !((byte[])reader[2]).AsSpan().SequenceEqual(streamBinary))
        {
            throw new InvalidOperationException(
                "Production-context processor identity hash collision detected.");
        }

        return reader.GetInt64(0);
    }

    private static async Task<ulong?> ReadCheckpointPositionAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long processorRowId,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT Position FROM dbo.ProductionContextCheckpoint" + hint +
            " WHERE ProductionContextProcessorRowId = @ProcessorRowId;";
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : SqlServerUInt64.Materialize((decimal)result);
    }

    private static async Task WriteCheckpointAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        ulong? current,
        ulong next,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (current is null)
        {
            command.CommandText =
                "INSERT INTO dbo.ProductionContextCheckpoint " +
                "(ProductionContextProcessorRowId, Position) VALUES (@ProcessorRowId, @Position);";
        }
        else
        {
            command.CommandText =
                "UPDATE dbo.ProductionContextCheckpoint SET Position = @Position " +
                "WHERE ProductionContextProcessorRowId = @ProcessorRowId AND Position = @Expected;";
            command.Parameters.Add(SqlServerUInt64.CreateParameter("@Expected", current.Value));
        }

        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", next));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "Production context processing checkpoint conflict.");
        }
    }

    private static async Task PersistOutputAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableName,
        string identity,
        string payload,
        string kind,
        CancellationToken cancellationToken)
    {
        var identityBinary = OrdinalStringKeyCodec.Encode(identity);
        var identityHash = SHA256.HashData(identityBinary);
        var payloadHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            $"SELECT IdentityBinary, PayloadHash, Payload FROM {tableName} WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE IdentityHash = @IdentityHash;";
        read.Parameters.Add("@IdentityHash", SqlDbType.Binary, 32).Value = identityHash;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var sameIdentity = ((byte[])reader[0]).AsSpan().SequenceEqual(identityBinary);
            var sameHash = ((byte[])reader[1]).AsSpan().SequenceEqual(payloadHash);
            var samePayload = string.Equals(reader.GetString(2), payload, StringComparison.Ordinal);
            if (!sameIdentity || !sameHash || !samePayload)
            {
                throw new InvalidOperationException(
                    $"Production context processing {kind} identity collides with different content.");
            }

            return;
        }

        await reader.DisposeAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"INSERT INTO {tableName} " +
            "(IdentityHash, IdentityBinary, IdentityText, PayloadHash, Payload) " +
            "VALUES (@IdentityHash, @IdentityBinary, @IdentityText, @PayloadHash, @Payload);";
        insert.Parameters.Add("@IdentityHash", SqlDbType.Binary, 32).Value = identityHash;
        insert.Parameters.Add("@IdentityBinary", SqlDbType.VarBinary, 512).Value = identityBinary;
        AddString(insert, "@IdentityText", identity, 256);
        insert.Parameters.Add("@PayloadHash", SqlDbType.Binary, 32).Value = payloadHash;
        insert.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payload;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateMetricInputEquivalence(
        IReadOnlyList<DurableMetricInputFact> metricFacts,
        IReadOnlyList<DurableMetricInputAppend> metricInputs)
    {
        if (metricFacts.Count != metricInputs.Count)
        {
            throw new InvalidOperationException(
                "Production context processing metric facts and positioned metric inputs must contain the same durable facts.");
        }

        var inputFacts = metricInputs.ToDictionary(
            static item => item.Fact.Id,
            static item => item.Fact);
        foreach (var fact in metricFacts)
        {
            if (!inputFacts.TryGetValue(fact.Id, out var inputFact) || inputFact != fact)
            {
                throw new InvalidOperationException(
                    "Production context processing metric facts and positioned metric inputs must contain equivalent payloads.");
            }
        }
    }

    private static void ValidateUnique<T>(IEnumerable<T> values, string kind)
        where T : notnull
    {
        var seen = new HashSet<T>();
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new InvalidOperationException(
                    $"Production context processing commit contains duplicate {kind} identity.");
            }
        }
    }

    private static void AddString(SqlCommand command, string name, string value, int size) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;
}
