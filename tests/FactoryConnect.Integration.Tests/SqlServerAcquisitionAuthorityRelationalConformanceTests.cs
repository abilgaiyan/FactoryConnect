using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAcquisitionAuthorityRelationalConformanceTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private const string BeyondUInt64Literal = "18446744073709551616";

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerAcquisitionAuthorityRelationalConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthorityRequiresCheckpointAndRemainsOneRowPerExactStream()
    {
        var missingCheckpointStream = StreamId();

        await AssertSqlRejectedAsync(
            """
            INSERT INTO dbo.AcquisitionContactAuthority
            (
                MachineId,
                StreamKeyBinary,
                SuccessfulContactTime,
                RawAcceptedThrough,
                AcquisitionRevision
            )
            VALUES
            (
                @MachineId,
                @StreamKeyBinary,
                '2026-09-12T07:00:00+00:00',
                NULL,
                0
            );
            """,
            missingCheckpointStream);

        var streamId = StreamId();
        var store = new SqlServerObservationIngestionStore(_fixture.ConnectionString);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 42, 101),
                [],
                new DateTimeOffset(2026, 9, 12, 7, 10, 0, TimeSpan.Zero)));

        await AssertSqlRejectedAsync(
            """
            INSERT INTO dbo.AcquisitionContactAuthority
            (
                MachineId,
                StreamKeyBinary,
                SuccessfulContactTime,
                RawAcceptedThrough,
                AcquisitionRevision
            )
            VALUES
            (
                @MachineId,
                @StreamKeyBinary,
                '2026-09-12T07:11:00+00:00',
                NULL,
                1
            );
            """,
            streamId);

        Assert.NotNull(await store.ReadAcquisitionContactAuthorityAsync(streamId));
    }

    [Fact]
    public async Task RawAcceptedThroughRejectsValuesAboveUInt64Range()
    {
        var streamId = StreamId();
        var store = new SqlServerObservationIngestionStore(_fixture.ConnectionString);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 42, 101),
                [],
                new DateTimeOffset(2026, 9, 12, 7, 20, 0, TimeSpan.Zero)));

        await AssertSqlRejectedAsync(
            $"""
            UPDATE dbo.AcquisitionContactAuthority
            SET RawAcceptedThrough = {BeyondUInt64Literal}
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """,
            streamId);

        var authority = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);
        Assert.Null(authority.RawAcceptedThrough);
    }

    private async Task AssertSqlRejectedAsync(
        string commandText,
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("@MachineId", streamId.MachineId.Value);
        command.Parameters.AddWithValue(
            "@StreamKeyBinary",
            OrdinalStringKeyCodec.Encode(streamId.StreamKey));

        await Assert.ThrowsAsync<SqlException>(
            () => command.ExecuteNonQueryAsync());
    }

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), $"MTConnect:{Guid.NewGuid():N}");
}
