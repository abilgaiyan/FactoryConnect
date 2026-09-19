using System.Data;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerCurrentStateAuthorityCutProvider :
    ICurrentStateAuthorityCutProvider
{
    private readonly string _connectionString;

    public SqlServerCurrentStateAuthorityCutProvider(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
        CurrentStateAuthorityBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        var streamKey = OrdinalStringKeyCodec.Encode(binding.ObservationStreamId.StreamKey);
        var mappingKey = StringOrderKeyV2Codec.Encode(binding.MappingProcessorId.Value);
        var stateKey = StringOrderKeyV2Codec.Encode(binding.StateProcessorId.Value);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken).ConfigureAwait(false);

            try
            {
                var acquisition = await ReadAcquisitionAsync(
                    connection, transaction, binding, streamKey, cancellationToken).ConfigureAwait(false);
                var mapping = await ReadMappingAsync(
                    connection, transaction, binding, streamKey, mappingKey, cancellationToken).ConfigureAwait(false);
                var evaluation = await ReadEvaluationAsync(
                    connection, transaction, binding, streamKey, stateKey, cancellationToken).ConfigureAwait(false);

                if (!IsConsistent(binding, acquisition, mapping, evaluation))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return StableCurrentStateAuthorityCutUnavailable.Instance;
                }

                var cut = new StableCurrentStateAuthorityCut(
                    new CurrentStateAuthorityCut(binding, acquisition, mapping, evaluation));
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return cut;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Current-state authority cut read was cancelled.",
                exception,
                cancellationToken);
        }
    }

    private static async Task<AcquisitionContactAuthority?> ReadAcquisitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentStateAuthorityBinding binding,
        byte[] streamKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SuccessfulContactTime, RawAcceptedThrough, AcquisitionRevision
            FROM dbo.AcquisitionContactAuthority
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;
        AddStreamParameters(command, binding.ObservationStreamId, streamKey);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var rawAcceptedThrough = reader.IsDBNull(1)
                ? null
                : new ObservationPosition(SqlServerUInt64.Materialize(reader.GetDecimal(1)));
            return new AcquisitionContactAuthority(
                binding.ObservationStreamId,
                reader.GetDateTimeOffset(0),
                rawAcceptedThrough,
                new AcquisitionAuthorityRevision(SqlServerUInt64.Materialize(reader.GetDecimal(2))));
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException or InvalidCastException)
        {
            throw Corruption("Persisted acquisition contact authority is invalid.", exception);
        }
    }

    private static async Task<MappingCoverageAuthority?> ReadMappingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentStateAuthorityBinding binding,
        byte[] streamKey,
        byte[] processorKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MappingProcessorId, MappingProcessorIdOrderKey, RawConsumedThrough,
                   MappedEvaluationInputHighWater, MappingRevision
            FROM dbo.MappingCoverageAuthority
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND (MappingProcessorIdOrderKey = @ProcessorOrderKey OR MappingProcessorId = @ProcessorId)
            ORDER BY MappingProcessorIdOrderKey;
            """;
        AddProcessorParameters(
            command, binding.ObservationStreamId, streamKey,
            binding.MappingProcessorId, processorKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var persistedProcessor = reader.GetString(0);
        var persistedKey = (byte[])reader[1];
        if (!string.Equals(persistedProcessor, binding.MappingProcessorId.Value, StringComparison.Ordinal) ||
            !persistedKey.AsSpan().SequenceEqual(processorKey) ||
            !persistedKey.AsSpan().SequenceEqual(StringOrderKeyV2Codec.Encode(persistedProcessor)))
        {
            throw Corruption("Persisted mapping coverage authority processor identity is inconsistent.");
        }

        MappingCoverageAuthority result;
        try
        {
            var rawConsumed = new ObservationPosition(
                SqlServerUInt64.Materialize(reader.GetDecimal(2)));
            var mappedHighWater = reader.IsDBNull(3)
                ? null
                : new ObservationPosition(
                    SqlServerUInt64.Materialize(reader.GetDecimal(3)));

            if (mappedHighWater is not null && mappedHighWater > rawConsumed)
            {
                throw Corruption(
                    "Persisted mapping coverage authority mapped high-water exceeds raw consumed coverage.");
            }

            result = new MappingCoverageAuthority(
                binding.MappingProcessorId,
                binding.ObservationStreamId,
                rawConsumed,
                mappedHighWater,
                new MappingAuthorityRevision(
                    SqlServerUInt64.Materialize(reader.GetDecimal(4))));
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

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Corruption("Persisted mapping coverage authority has conflicting rows for one semantic identity.");
        }

        return result;
    }

    private static async Task<EvaluationAuthority?> ReadEvaluationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CurrentStateAuthorityBinding binding,
        byte[] streamKey,
        byte[] processorKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT StateProcessorId, StateProcessorIdOrderKey, Position, MachineState,
                   LastConsumedInstanceId, ContinuityPolicyIdentity,
                   ContinuityPolicyVersion, ProjectionRevision
            FROM dbo.MachineStateActivityAuthority
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND (StateProcessorIdOrderKey = @ProcessorOrderKey OR StateProcessorId = @ProcessorId)
            ORDER BY StateProcessorIdOrderKey;
            """;
        AddProcessorParameters(
            command, binding.ObservationStreamId, streamKey,
            binding.StateProcessorId, processorKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var persistedProcessor = reader.GetString(0);
        var persistedKey = (byte[])reader[1];
        if (!string.Equals(persistedProcessor, binding.StateProcessorId.Value, StringComparison.Ordinal) ||
            !persistedKey.AsSpan().SequenceEqual(processorKey) ||
            !persistedKey.AsSpan().SequenceEqual(StringOrderKeyV2Codec.Encode(persistedProcessor)))
        {
            throw Corruption("Persisted machine state/activity processor identity is inconsistent.");
        }

        var state = (MachineState)reader.GetByte(3);
        if (!Enum.IsDefined(state))
        {
            throw Corruption("Persisted machine state is invalid.");
        }

        EvaluationAuthority result;
        try
        {
            result = new EvaluationAuthority(
                binding.StateProcessorId,
                binding.ObservationStreamId,
                new ObservationPosition(SqlServerUInt64.Materialize(reader.GetDecimal(2))),
                state,
                SqlServerUInt64.Materialize(reader.GetDecimal(4)),
                new CurrentStatePolicyReference(reader.GetString(5), reader.GetString(6)),
                new StateProjectionAuthorityRevision(SqlServerUInt64.Materialize(reader.GetDecimal(7))));
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException or InvalidCastException)
        {
            throw Corruption("Persisted evaluation authority is invalid.", exception);
        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Corruption("Persisted machine state/activity authority has conflicting semantic rows.");
        }

        return result;
    }

    private static bool IsConsistent(
        CurrentStateAuthorityBinding binding,
        AcquisitionContactAuthority? acquisition,
        MappingCoverageAuthority? mapping,
        EvaluationAuthority? evaluation)
    {
        if (acquisition is not null &&
            acquisition.ObservationStreamId != binding.ObservationStreamId)
        {
            return false;
        }

        if (mapping is not null &&
            (mapping.MappingProcessorId != binding.MappingProcessorId ||
             mapping.ObservationStreamId != binding.ObservationStreamId ||
             (mapping.MappedEvaluationInputHighWater is not null &&
              mapping.MappedEvaluationInputHighWater > mapping.RawConsumedThrough)))
        {
            return false;
        }

        if (evaluation is not null &&
            (evaluation.StateProcessorId != binding.StateProcessorId ||
             evaluation.ObservationStreamId != binding.ObservationStreamId))
        {
            return false;
        }

        if (acquisition is not null &&
            mapping is not null &&
            (acquisition.RawAcceptedThrough is null ||
             mapping.RawConsumedThrough > acquisition.RawAcceptedThrough))
        {
            return false;
        }

        return mapping is null ||
               evaluation is null ||
               (mapping.MappedEvaluationInputHighWater is not null &&
                evaluation.EvaluatedThrough <= mapping.MappedEvaluationInputHighWater);
    }

    private static void AddStreamParameters(
        SqlCommand command,
        ObservationStreamId streamId,
        byte[] streamKey)
    {
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value = streamId.MachineId.Value;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            OrdinalStringKeyCodec.MaxCodeUnits * 2).Value = streamKey;
    }

    private static void AddProcessorParameters(
        SqlCommand command,
        ObservationStreamId streamId,
        byte[] streamKey,
        ObservationProcessorId processorId,
        byte[] processorKey)
    {
        AddStreamParameters(command, streamId, streamKey);
        command.Parameters.Add("@ProcessorId", SqlDbType.NVarChar, 256).Value = processorId.Value;
        command.Parameters.Add(
            "@ProcessorOrderKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value = processorKey;
    }

    private static InvalidOperationException Corruption(
        string message,
        Exception? inner = null) =>
        new(message, inner);
}
