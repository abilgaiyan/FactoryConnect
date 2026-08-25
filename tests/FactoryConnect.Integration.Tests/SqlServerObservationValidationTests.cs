using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerObservationValidationTests
{
    [Theory]
    [InlineData(99)]
    [InlineData(256)]
    [InlineData(-256)]
    public async Task InvalidObservationQualityIsRejectedBeforeSqlMutation(
        int qualityValue)
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "mtconnect:test");
        var checkpoint = new ObservationCheckpoint(streamId, 10, 2);
        var observation = new SequencedMachineObservation(
            1,
            new MachineObservation
            {
                MachineId = streamId.MachineId,
                Source = "mtconnect",
                Address = "load",
                Type = SignalType.Numeric,
                Value = 42.5m,
                Quality = (ObservationQuality)qualityValue,
                Timestamp = DateTimeOffset.UtcNow,
            });
        var store = new SqlServerObservationIngestionStore(
            "Server=invalid;Database=invalid;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.CommitAsync(
                new ObservationIngestionBatch(
                    null,
                    checkpoint,
                    [observation])));

        Assert.Contains(
            "Observation quality",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(256)]
    [InlineData(-256)]
    public async Task InvalidSignalTypeIsRejectedBeforeSqlMutation(
        int signalTypeValue)
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "mtconnect:test");
        var checkpoint = new ObservationCheckpoint(streamId, 10, 2);
        var observation = new SequencedMachineObservation(
            1,
            new MachineObservation
            {
                MachineId = streamId.MachineId,
                Source = "mtconnect",
                Address = "load",
                Type = (SignalType)signalTypeValue,
                Value = 42.5m,
                Quality = ObservationQuality.Good,
                Timestamp = DateTimeOffset.UtcNow,
            });
        var store = new SqlServerObservationIngestionStore(
            "Server=invalid;Database=invalid;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.CommitAsync(
                new ObservationIngestionBatch(
                    null,
                    checkpoint,
                    [observation])));

        Assert.Contains(
            "Observation signal type",
            exception.Message,
            StringComparison.Ordinal);
    }
}
