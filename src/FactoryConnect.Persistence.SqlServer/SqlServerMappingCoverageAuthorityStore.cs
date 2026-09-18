using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerMappingCoverageAuthorityStore :
    IMappingCoverageAuthorityStore
{
    private readonly string _connectionString;

    public SqlServerMappingCoverageAuthorityStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask<MappingCoverageAuthority?> ReadAsync(
        ObservationProcessorId mappingProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappingProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        cancellationToken.ThrowIfCancellationRequested();

        var streamKeyBinary = OrdinalStringKeyCodec.Encode(observationStreamId.StreamKey);
        var processorOrderKey = StringOrderKeyV2Codec.Encode(mappingProcessorId.Value);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await ReadCurrentAsync(
                connection,
                transaction: null,
                mappingProcessorId,
                observationStreamId,
                streamKeyBinary,
                processorOrderKey,
                lockForUpdate: false,
                cancellationToken);
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Mapping coverage authority read was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async ValueTask CommitAsync(
        MappingCoverageCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        var streamId = commit.ObservationStreamId;
        var processorId = commit.MappingProcessorId;
        var streamKeyBinary = OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        var processorOrderKey = StringOrderKeyV2Codec.Encode(processorId.Value);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var sqlTransaction = (SqlTransaction)transaction;

            try
            {
                var current = await ReadCurrentAsync(
                    connection,
                    sqlTransaction,
                    processorId,
                    streamId,
                    streamKeyBinary,
                    processorOrderKey,
                    lockForUpdate: true,
                    cancellationToken);

                if (current != commit.ExpectedAuthority)
                {
                    if (current is not null && SameCoverage(current, commit))
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return;
                    }

                    throw new InvalidOperationException(
                        "The mapping coverage authority no longer matches the expected state.");
                }

                if (current is not null && SameCoverage(current, commit))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                if (current is null)
                {
                    await InsertAsync(
                        connection,
                        sqlTransaction,
                        commit,
                        streamKeyBinary,
                        processorOrderKey,
                        cancellationToken);
                }
                else
                {
                    var nextRevision = NextRevision(current.MappingRevision);
                    await UpdateAsync(
                        connection,
                        sqlTransaction,
                        commit,
                        streamKeyBinary,
                        processorOrderKey,
                        current.MappingRevision.Value,
                        nextRevision,
                        cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Mapping coverage authority commit was cancelled.",
                exception,
                cancellationToken);
        }
    }

    private static async Task<MappingCoverageAuthority?> ReadCurrentAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        ObservationProcessorId mappingProcessorId,
        ObservationStreamId observationStreamId,
        byte[] streamKeyBinary,
        byte[] processorOrderKey,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        command.CommandText =
            "SELECT MappingProcessorId, MappingProcessorIdOrderKey, RawConsumedThrough, " +
            "MappedEvaluationInputHighWater, MappingRevision " +
            "FROM dbo.MappingCoverageAuthority" + hint + " " +
            "WHERE MachineId = @MachineId AND StreamKeyBinary = @StreamKeyBinary AND " +
            "(MappingProcessorIdOrderKey = @MappingProcessorIdOrderKey OR MappingProcessorId = @MappingProcessorId) " +
            "ORDER BY MappingProcessorIdOrderKey;";

        AddIdentityParameters(
            command,
            mappingProcessorId,
            observationStreamId,
            streamKeyBinary,
            processorOrderKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        MappingCoverageAuthority authority;
        try
        {
            authority = Materialize(
                mappingProcessorId,
                observationStreamId,
                processorOrderKey,
                reader);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            OverflowException or
            InvalidCastException)
        {
            throw Corruption(
                "Persisted mapping coverage authority is invalid.",
                exception);
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw Corruption(
                "Persisted mapping coverage authority has conflicting rows for one semantic identity.");
        }

        return authority;
    }

    private static MappingCoverageAuthority Materialize(
        ObservationProcessorId expectedProcessorId,
        ObservationStreamId observationStreamId,
        byte[] expectedProcessorOrderKey,
        SqlDataReader reader)
    {
        var persistedProcessorId = reader.GetString(0);
        var persistedProcessorOrderKey = (byte[])reader[1];

        if (string.IsNullOrWhiteSpace(persistedProcessorId) ||
            !string.Equals(
                persistedProcessorId,
                expectedProcessorId.Value,
                StringComparison.Ordinal) ||
            !persistedProcessorOrderKey.AsSpan().SequenceEqual(expectedProcessorOrderKey) ||
            !persistedProcessorOrderKey.AsSpan().SequenceEqual(
                StringOrderKeyV2Codec.Encode(persistedProcessorId)))
        {
            throw Corruption(
                "Persisted mapping coverage authority processor identity is inconsistent.");
        }

        var rawConsumedThrough = new ObservationPosition(
            SqlServerUInt64.Materialize(reader.GetDecimal(2)));
        var mappedHighWater = reader.IsDBNull(3)
            ? null
            : new ObservationPosition(
                SqlServerUInt64.Materialize(reader.GetDecimal(3)));

        if (mappedHighWater is not null && mappedHighWater > rawConsumedThrough)
        {
            throw Corruption(
                "Persisted mapping coverage authority mapped high-water exceeds raw consumed coverage.");
        }

        return new MappingCoverageAuthority(
            expectedProcessorId,
            observationStreamId,
            rawConsumedThrough,
            mappedHighWater,
            new MappingAuthorityRevision(
                SqlServerUInt64.Materialize(reader.GetDecimal(4))));
    }

    private static async Task InsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MappingCoverageCommit commit,
        byte[] streamKeyBinary,
        byte[] processorOrderKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.MappingCoverageAuthority
                (MachineId, StreamKeyBinary, MappingProcessorId,
                 MappingProcessorIdOrderKey, RawConsumedThrough,
                 MappedEvaluationInputHighWater, MappingRevision)
            VALUES
                (@MachineId, @StreamKeyBinary, @MappingProcessorId,
                 @MappingProcessorIdOrderKey, @RawConsumedThrough,
                 @MappedEvaluationInputHighWater, @MappingRevision);
            """;

        AddIdentityParameters(
            command,
            commit.MappingProcessorId,
            commit.ObservationStreamId,
            streamKeyBinary,
            processorOrderKey);
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@RawConsumedThrough",
                commit.RawConsumedThrough.Value));
        AddNullableUInt64(
            command,
            "@MappedEvaluationInputHighWater",
            commit.MappedEvaluationInputHighWater?.Value);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@MappingRevision", 0));

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Mapping coverage authority initial publication failed.");
        }
    }

    private static async Task UpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MappingCoverageCommit commit,
        byte[] streamKeyBinary,
        byte[] processorOrderKey,
        ulong expectedRevision,
        ulong nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.MappingCoverageAuthority
            SET RawConsumedThrough = @RawConsumedThrough,
                MappedEvaluationInputHighWater = @MappedEvaluationInputHighWater,
                MappingRevision = @NextRevision
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND MappingProcessorIdOrderKey = @MappingProcessorIdOrderKey
              AND MappingRevision = @ExpectedRevision;
            """;

        AddIdentityParameters(
            command,
            commit.MappingProcessorId,
            commit.ObservationStreamId,
            streamKeyBinary,
            processorOrderKey);
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter(
                "@RawConsumedThrough",
                commit.RawConsumedThrough.Value));
        AddNullableUInt64(
            command,
            "@MappedEvaluationInputHighWater",
            commit.MappedEvaluationInputHighWater?.Value);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@ExpectedRevision", expectedRevision));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@NextRevision", nextRevision));

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Mapping coverage authority revision conflict.");
        }
    }

    private static void AddIdentityParameters(
        SqlCommand command,
        ObservationProcessorId mappingProcessorId,
        ObservationStreamId observationStreamId,
        byte[] streamKeyBinary,
        byte[] processorOrderKey)
    {
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            observationStreamId.MachineId.Value;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            OrdinalStringKeyCodec.MaxCodeUnits * 2).Value = streamKeyBinary;
        command.Parameters.Add(
            "@MappingProcessorIdOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value = processorOrderKey;
        command.Parameters.Add(
            "@MappingProcessorId",
            SqlDbType.NVarChar,
            256).Value = mappingProcessorId.Value;
    }

    private static void AddNullableUInt64(
        SqlCommand command,
        string name,
        ulong? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 20;
        parameter.Scale = 0;
        parameter.Value = value is null
            ? DBNull.Value
            : checked((decimal)value.Value);
    }

    private static ulong NextRevision(MappingAuthorityRevision current)
    {
        if (current.Value == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The mapping authority revision space is exhausted.");
        }

        return current.Value + 1;
    }

    private static bool SameCoverage(
        MappingCoverageAuthority current,
        MappingCoverageCommit proposed) =>
        current.MappingProcessorId == proposed.MappingProcessorId &&
        current.ObservationStreamId == proposed.ObservationStreamId &&
        current.RawConsumedThrough == proposed.RawConsumedThrough &&
        current.MappedEvaluationInputHighWater == proposed.MappedEvaluationInputHighWater;

    private static InvalidOperationException Corruption(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);
}
