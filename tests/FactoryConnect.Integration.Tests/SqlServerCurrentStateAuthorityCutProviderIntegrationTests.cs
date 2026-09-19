using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerCurrentStateAuthorityCutProviderIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerCurrentStateAuthorityCutProviderIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AllNullAuthoritiesReturnStableCut()
    {
        var binding = await CreateBindingAsync();
        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await CreateProvider().ReadAuthorityCutAsync(binding, CancellationToken.None));

        Assert.Null(result.Cut.AcquisitionContact);
        Assert.Null(result.Cut.MappingCoverage);
        Assert.Null(result.Cut.Evaluation);
    }

    [Fact]
    public async Task PartialAcquisitionAuthorityReturnsStableCut()
    {
        var binding = await CreateBindingAsync();
        await InsertAcquisitionAsync(binding, rawAcceptedThrough: null, revision: 3);

        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await CreateProvider().ReadAuthorityCutAsync(binding, CancellationToken.None));

        Assert.Equal(3UL, result.Cut.AcquisitionContact!.AcquisitionRevision.Value);
        Assert.Null(result.Cut.MappingCoverage);
        Assert.Null(result.Cut.Evaluation);
    }

    [Fact]
    public async Task CompleteConsistentLineageReturnsStableCut()
    {
        var binding = await CreateBindingAsync();
        await InsertObservationAsync(binding, 5);
        await InsertAcquisitionAsync(binding, 5, 2);
        await InsertMappingAsync(binding, 5, 5, 4);
        await InsertEvaluationAsync(binding, 5, 6);

        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await CreateProvider().ReadAuthorityCutAsync(binding, CancellationToken.None));

        Assert.Equal(5UL, result.Cut.AcquisitionContact!.RawAcceptedThrough!.Value);
        Assert.Equal(5UL, result.Cut.MappingCoverage!.RawConsumedThrough.Value);
        Assert.Equal(5UL, result.Cut.Evaluation!.EvaluatedThrough.Value);
        Assert.Equal(6UL, result.Cut.Evaluation.ProjectionRevision.Value);
    }

    [Fact]
    public async Task ImpossibleCrossAuthorityLineageReturnsUnavailable()
    {
        var binding = await CreateBindingAsync();
        await InsertObservationAsync(binding, 1);
        await InsertAcquisitionAsync(binding, 1, 0);
        await InsertMappingAsync(binding, 2, 2, 0);

        Assert.IsType<StableCurrentStateAuthorityCutUnavailable>(
            await CreateProvider().ReadAuthorityCutAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task SerializableCutWaitsForConcurrentMappingWriterAndReadsCommittedRevision()
    {
        var binding = await CreateBindingAsync();
        await InsertObservationAsync(binding, 5);
        await InsertAcquisitionAsync(binding, 5, 0);
        await InsertMappingAsync(binding, 5, 5, 0);

        await using var writer = _fixture.CreateConnection();
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync(
            IsolationLevel.Serializable);
        await using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE dbo.MappingCoverageAuthority
                SET MappingRevision = 1
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary
                  AND MappingProcessorIdOrderKey = @ProcessorKey;
                """;
            AddStreamParameters(command, binding);
            command.Parameters.Add(
                "@ProcessorKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(binding.MappingProcessorId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var read = CreateProvider().ReadAuthorityCutAsync(binding, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(read.IsCompleted);

        await transaction.CommitAsync();

        var result = Assert.IsType<StableCurrentStateAuthorityCut>(
            await read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1UL, result.Cut.MappingCoverage!.MappingRevision.Value);
    }

    [Fact]
    public async Task CancellationWhileWaitingForWriterLockPropagates()
    {
        var binding = await CreateBindingAsync();
        await InsertObservationAsync(binding, 5);
        await InsertAcquisitionAsync(binding, 5, 0);
        await InsertMappingAsync(binding, 5, 5, 0);

        await using var writer = _fixture.CreateConnection();
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync(
            IsolationLevel.Serializable);
        await using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE dbo.MappingCoverageAuthority
                SET MappingRevision = MappingRevision
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary
                  AND MappingProcessorIdOrderKey = @ProcessorKey;
                """;
            AddStreamParameters(command, binding);
            command.Parameters.Add(
                "@ProcessorKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(binding.MappingProcessorId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        using var cancellation = new CancellationTokenSource();
        var read = CreateProvider().ReadAuthorityCutAsync(binding, cancellation.Token);
        await Task.Delay(200);
        Assert.False(read.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => read.WaitAsync(TimeSpan.FromSeconds(10)));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task CorruptMappingUInt64IsTranslatedToInvalidOperationException()
    {
        var binding = await CreateBindingAsync();
        await InsertMappingAsync(binding, 1, 1, 0);

        await using (var connection = _fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE dbo.MappingCoverageAuthority
                    NOCHECK CONSTRAINT CK_MappingCoverageAuthority_RawConsumedThrough_UInt64Positive;
                UPDATE dbo.MappingCoverageAuthority
                SET RawConsumedThrough = 99999999999999999999,
                    MappedEvaluationInputHighWater = NULL
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary
                  AND MappingProcessorIdOrderKey = @ProcessorKey;
                """;
            AddStreamParameters(command, binding);
            command.Parameters.Add(
                "@ProcessorKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(binding.MappingProcessorId.Value);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => CreateProvider().ReadAuthorityCutAsync(binding, CancellationToken.None));
            Assert.Contains("mapping coverage authority", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<OverflowException>(exception.InnerException);
        }
        finally
        {
            await using var connection = _fixture.CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM dbo.MappingCoverageAuthority
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary
                  AND MappingProcessorIdOrderKey = @ProcessorKey;
                ALTER TABLE dbo.MappingCoverageAuthority
                    WITH CHECK CHECK CONSTRAINT CK_MappingCoverageAuthority_RawConsumedThrough_UInt64Positive;
                """;
            AddStreamParameters(command, binding);
            command.Parameters.Add(
                "@ProcessorKey",
                SqlDbType.VarBinary,
                StringOrderKeyV2Codec.MaximumEncodedLength).Value =
                StringOrderKeyV2Codec.Encode(binding.MappingProcessorId.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PreCancelledReadDoesNotOpenAuthorityCut()
    {
        var binding = await CreateBindingAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProvider().ReadAuthorityCutAsync(binding, cancellation.Token));
    }

    private SqlServerCurrentStateAuthorityCutProvider CreateProvider() =>
        new(_fixture.ConnectionString);

    private async Task<CurrentStateAuthorityBinding> CreateBindingAsync()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            $"MTConnect:{Guid.NewGuid():N}");
        var binding = new CurrentStateAuthorityBinding(
            streamId.MachineId,
            streamId,
            new ObservationProcessorId($"mapping-{Guid.NewGuid():N}"),
            new ObservationProcessorId($"state-{Guid.NewGuid():N}"));

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.ObservationStreamCheckpoint
                (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
            VALUES (@MachineId, @StreamKeyBinary, @StreamKey, 1, 10);
            """;
        AddStreamParameters(command, binding);
        command.Parameters.Add("@StreamKey", SqlDbType.NVarChar, 256).Value =
            streamId.StreamKey;
        await command.ExecuteNonQueryAsync();
        return binding;
    }

    private async Task InsertObservationAsync(
        CurrentStateAuthorityBinding binding,
        ulong position)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.MachineObservation
                (MachineId, StreamKeyBinary, InstanceId, Sequence, Source, Address,
                 SignalType, ObservationValue, Quality, ObservedAt, Position)
            VALUES
                (@MachineId, @StreamKeyBinary, 1, @Sequence, N'test', N'X',
                 0, N'true', 0, @ObservedAt, @Position);
            """;
        AddStreamParameters(command, binding);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Sequence", position));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position));
        command.Parameters.Add("@ObservedAt", SqlDbType.DateTimeOffset).Value = Stamp;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertAcquisitionAsync(
        CurrentStateAuthorityBinding binding,
        ulong? rawAcceptedThrough,
        ulong revision)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.AcquisitionContactAuthority
                (MachineId, StreamKeyBinary, SuccessfulContactTime,
                 RawAcceptedThrough, AcquisitionRevision)
            VALUES
                (@MachineId, @StreamKeyBinary, @SuccessfulContactTime,
                 @RawAcceptedThrough, @Revision);
            """;
        AddStreamParameters(command, binding);
        command.Parameters.Add("@SuccessfulContactTime", SqlDbType.DateTimeOffset).Value = Stamp;
        command.Parameters.Add(CreateNullableUInt64Parameter(
            "@RawAcceptedThrough",
            rawAcceptedThrough));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", revision));
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertMappingAsync(
        CurrentStateAuthorityBinding binding,
        ulong rawConsumedThrough,
        ulong? mappedHighWater,
        ulong revision)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.MappingCoverageAuthority
                (MachineId, StreamKeyBinary, MappingProcessorId,
                 MappingProcessorIdOrderKey, RawConsumedThrough,
                 MappedEvaluationInputHighWater, MappingRevision)
            VALUES
                (@MachineId, @StreamKeyBinary, @ProcessorId,
                 @ProcessorKey, @RawConsumedThrough,
                 @MappedHighWater, @Revision);
            """;
        AddStreamParameters(command, binding);
        command.Parameters.Add("@ProcessorId", SqlDbType.NVarChar, 256).Value =
            binding.MappingProcessorId.Value;
        command.Parameters.Add(
            "@ProcessorKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(binding.MappingProcessorId.Value);
        command.Parameters.Add(
            SqlServerUInt64.CreateParameter("@RawConsumedThrough", rawConsumedThrough));
        command.Parameters.Add(CreateNullableUInt64Parameter(
            "@MappedHighWater",
            mappedHighWater));
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", revision));
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertEvaluationAsync(
        CurrentStateAuthorityBinding binding,
        ulong position,
        ulong revision)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT dbo.MachineStateActivityAuthority
                (MachineId, StreamKeyBinary, StateProcessorId,
                 StateProcessorIdOrderKey, Position, MachineState,
                 ActiveState, ActiveStartedAt, LastConsumedInstanceId,
                 ContinuityPolicyIdentity, ContinuityPolicyVersion,
                 ProjectionRevision)
            VALUES
                (@MachineId, @StreamKeyBinary, @ProcessorId,
                 @ProcessorKey, @Position, @MachineState,
                 NULL, NULL, 1, N'continuity/default', N'1.0',
                 @Revision);
            """;
        AddStreamParameters(command, binding);
        command.Parameters.Add("@ProcessorId", SqlDbType.NVarChar, 256).Value =
            binding.StateProcessorId.Value;
        command.Parameters.Add(
            "@ProcessorKey",
            SqlDbType.VarBinary,
            StringOrderKeyV2Codec.MaximumEncodedLength).Value =
            StringOrderKeyV2Codec.Encode(binding.StateProcessorId.Value);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position));
        command.Parameters.Add("@MachineState", SqlDbType.TinyInt).Value =
            (byte)MachineState.Running;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Revision", revision));
        await command.ExecuteNonQueryAsync();
    }

    private static SqlParameter CreateNullableUInt64Parameter(
        string name,
        ulong? value) =>
        new(name, SqlDbType.Decimal)
        {
            Precision = 20,
            Scale = 0,
            Value = value.HasValue ? checked((decimal)value.Value) : DBNull.Value,
        };

    private static void AddStreamParameters(
        SqlCommand command,
        CurrentStateAuthorityBinding binding)
    {
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            binding.MachineId.Value;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            OrdinalStringKeyCodec.MaxCodeUnits * 2).Value =
            OrdinalStringKeyCodec.Encode(binding.ObservationStreamId.StreamKey);
    }

    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
}
