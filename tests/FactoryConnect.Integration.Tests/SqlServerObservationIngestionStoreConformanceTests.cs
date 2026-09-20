using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerObservationIngestionStoreConformanceTests :
    ObservationIngestionStoreConformanceTests,
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerObservationIngestionStoreConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "true")]
    public async Task DigitalBooleanCommitPersistsCanonicalValueAndReplaysExactly(
        bool value,
        string expectedPersistedValue)
    {
        var store = new SqlServerObservationIngestionStore(
            _fixture.ConnectionString);
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "MTConnect:CNC-DIGITAL");
        var checkpoint = new ObservationCheckpoint(streamId, 7, 2);
        var batch = new ObservationIngestionBatch(
            null,
            checkpoint,
            [
                new SequencedMachineObservation(
                    1,
                    new MachineObservation
                    {
                        MachineId = streamId.MachineId,
                        Source = "modbus",
                        Address = "DI1",
                        Type = SignalType.Digital,
                        Value = value,
                        Quality = ObservationQuality.Good,
                        Timestamp = DateTimeOffset.UnixEpoch,
                    }),
            ],
            DateTimeOffset.UnixEpoch);

        await store.CommitAsync(batch);
        await store.CommitAsync(batch);

        Assert.Equal(
            checkpoint,
            await store.ReadCheckpointAsync(streamId));
        Assert.Equal(1, ReadObservationCount(store, streamId));

        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ObservationValue
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary
              AND InstanceId = 7
              AND Sequence = 1;
            """;
        command.Parameters.AddWithValue(
            "@MachineId",
            streamId.MachineId.Value);
        command.Parameters.AddWithValue(
            "@StreamKeyBinary",
            OrdinalStringKeyCodec.Encode(streamId.StreamKey));

        Assert.Equal(
            expectedPersistedValue,
            Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }

    protected override IObservationIngestionStore CreateStore() =>
        new SqlServerObservationIngestionStore(
            _fixture.ConnectionString);

    protected override int ReadObservationCount(
        IObservationIngestionStore store,
        ObservationStreamId streamId)
    {
        var sqlStore = Assert.IsType<SqlServerObservationIngestionStore>(store);
        using var connection = new SqlConnection(sqlStore.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;
        command.Parameters.AddWithValue(
            "@MachineId",
            streamId.MachineId.Value);
        command.Parameters.AddWithValue(
            "@StreamKeyBinary",
            OrdinalStringKeyCodec.Encode(streamId.StreamKey));

        return Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
