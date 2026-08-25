using System.Data;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerSchemaIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly decimal UInt64Maximum =
        18446744073709551615m;

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerSchemaIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EmbeddedSchemaCreatesRequiredTables()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.tables " +
            "WHERE name IN ('ObservationStreamCheckpoint', 'MachineObservation');";

        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task BinaryIdentityPreservesTrailingSpaceDistinction()
    {
        var machineId = Guid.NewGuid();

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        await InsertCheckpointAsync(
            connection,
            machineId,
            "MTConnect:CNC-01",
            1m,
            2m);
        await InsertCheckpointAsync(
            connection,
            machineId,
            "MTConnect:CNC-01 ",
            1m,
            2m);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.ObservationStreamCheckpoint " +
            "WHERE MachineId = @MachineId;";
        command.Parameters.Add(
            "@MachineId",
            SqlDbType.UniqueIdentifier).Value = machineId;

        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CheckpointRejectsValuesOutsideUInt64Range()
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        await Assert.ThrowsAsync<SqlException>(
            () => InsertCheckpointAsync(
                connection,
                Guid.NewGuid(),
                "negative-instance",
                -1m,
                0m));

        await Assert.ThrowsAsync<SqlException>(
            () => InsertCheckpointAsync(
                connection,
                Guid.NewGuid(),
                "large-next-sequence",
                0m,
                UInt64Maximum + 1m));
    }

    [Fact]
    public async Task ObservationRejectsValuesOutsideUInt64Range()
    {
        var machineId = Guid.NewGuid();
        const string streamKey = "observation-range";

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await InsertCheckpointAsync(
            connection,
            machineId,
            streamKey,
            1m,
            2m);

        await Assert.ThrowsAsync<SqlException>(
            () => InsertObservationAsync(
                connection,
                machineId,
                streamKey,
                -1m,
                1m,
                signalType: 4,
                quality: 0));

        await Assert.ThrowsAsync<SqlException>(
            () => InsertObservationAsync(
                connection,
                machineId,
                streamKey,
                1m,
                UInt64Maximum + 1m,
                signalType: 4,
                quality: 0));
    }

    [Fact]
    public async Task ObservationRejectsUnknownEnumValues()
    {
        var machineId = Guid.NewGuid();
        const string streamKey = "enum-range";

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await InsertCheckpointAsync(
            connection,
            machineId,
            streamKey,
            1m,
            3m);

        await Assert.ThrowsAsync<SqlException>(
            () => InsertObservationAsync(
                connection,
                machineId,
                streamKey,
                1m,
                1m,
                signalType: 8,
                quality: 0));

        await Assert.ThrowsAsync<SqlException>(
            () => InsertObservationAsync(
                connection,
                machineId,
                streamKey,
                1m,
                2m,
                signalType: 4,
                quality: 3));
    }

    private static async Task InsertCheckpointAsync(
        SqlConnection connection,
        Guid machineId,
        string streamKey,
        decimal instanceId,
        decimal nextSequence)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.ObservationStreamCheckpoint " +
            "(MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence) " +
            "VALUES (@MachineId, @StreamKeyBinary, @StreamKey, @InstanceId, @NextSequence);";
        command.Parameters.Add(
            "@MachineId",
            SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            512).Value = OrdinalStringKeyCodec.Encode(streamKey);
        command.Parameters.Add(
            "@StreamKey",
            SqlDbType.NVarChar,
            256).Value = streamKey;
        command.Parameters.Add(
            "@InstanceId",
            SqlDbType.Decimal).Value = instanceId;
        command.Parameters.Add(
            "@NextSequence",
            SqlDbType.Decimal).Value = nextSequence;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertObservationAsync(
        SqlConnection connection,
        Guid machineId,
        string streamKey,
        decimal instanceId,
        decimal sequence,
        byte signalType,
        byte quality)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.MachineObservation " +
            "(MachineId, StreamKeyBinary, InstanceId, Sequence, Source, Address, " +
            "SignalType, ObservationValue, Quality, ObservedAt) " +
            "VALUES (@MachineId, @StreamKeyBinary, @InstanceId, @Sequence, " +
            "@Source, @Address, @SignalType, @ObservationValue, @Quality, @ObservedAt);";
        command.Parameters.Add(
            "@MachineId",
            SqlDbType.UniqueIdentifier).Value = machineId;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            512).Value = OrdinalStringKeyCodec.Encode(streamKey);
        command.Parameters.Add(
            "@InstanceId",
            SqlDbType.Decimal).Value = instanceId;
        command.Parameters.Add(
            "@Sequence",
            SqlDbType.Decimal).Value = sequence;
        command.Parameters.Add(
            "@Source",
            SqlDbType.NVarChar,
            256).Value = "test";
        command.Parameters.Add(
            "@Address",
            SqlDbType.NVarChar,
            512).Value = "test";
        command.Parameters.Add(
            "@SignalType",
            SqlDbType.TinyInt).Value = signalType;
        command.Parameters.Add(
            "@ObservationValue",
            SqlDbType.NVarChar,
            -1).Value = "1";
        command.Parameters.Add(
            "@Quality",
            SqlDbType.TinyInt).Value = quality;
        command.Parameters.Add(
            "@ObservedAt",
            SqlDbType.DateTimeOffset).Value = DateTimeOffset.UtcNow;

        await command.ExecuteNonQueryAsync();
    }
}
