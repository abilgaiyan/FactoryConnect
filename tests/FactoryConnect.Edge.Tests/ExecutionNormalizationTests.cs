using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ExecutionNormalizationTests
{
    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("READY", false)]
    [InlineData("FEED_HOLD", false)]
    [InlineData("OPTIONAL_STOP", false)]
    [InlineData("PROGRAM_STOPPED", false)]
    [InlineData("STOPPED", false)]
    public async Task ExplicitExecutionValuesNormalizeWithoutMutatingRaw(string value, bool expected)
    {
        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:arbitrary-device");
        var inner = new CapturingProcessor();
        var processor = new ExecutionNormalizationProcessor(inner, stream, "mtconnect", "configured-execution");
        var raw = Raw(stream, value);
        await processor.ProcessAsync([raw]);
        var normalized = Assert.Single(inner.Observations);
        Assert.Equal(expected, Assert.IsType<bool>(normalized.Observation.Value));
        Assert.Equal(SignalType.Digital, normalized.Observation.Type);
        Assert.Equal(raw.Position, normalized.Position);
        Assert.Equal(raw.InstanceId, normalized.InstanceId);
        Assert.Equal(raw.Sequence, normalized.Sequence);
        Assert.Equal(raw.StreamId, normalized.StreamId);
        Assert.Equal(raw.Observation.Timestamp, normalized.Observation.Timestamp);
        Assert.Equal(raw.Observation.Source, normalized.Observation.Source);
        Assert.Equal(raw.Observation.Address, normalized.Observation.Address);
        Assert.Equal(SignalType.Enumeration, raw.Observation.Type);
        Assert.Equal(value, raw.Observation.Value);
        Assert.Equal(inner.ProcessorId, processor.ProcessorId);
    }

    [Theory]
    [InlineData("FUTURE_VALUE", ObservationQuality.Good, ObservationQuality.Uncertain)]
    [InlineData("active", ObservationQuality.Good, ObservationQuality.Uncertain)]
    [InlineData(null, ObservationQuality.Good, ObservationQuality.Uncertain)]
    [InlineData("ACTIVE", ObservationQuality.Bad, ObservationQuality.Bad)]
    [InlineData(null, ObservationQuality.Uncertain, ObservationQuality.Uncertain)]
    public async Task UnknownOrPoorQualityDoesNotInventRunningOrStopped(
        string? value, ObservationQuality quality, ObservationQuality expectedQuality)
    {
        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:device");
        var inner = new CapturingProcessor();
        var processor = new ExecutionNormalizationProcessor(inner, stream, "mtconnect", "configured-execution");
        await processor.ProcessAsync([Raw(stream, value, quality)]);
        var normalized = Assert.Single(inner.Observations).Observation;
        Assert.Null(normalized.Value);
        Assert.Equal(expectedQuality, normalized.Quality);
        Assert.Equal(SignalType.Digital, normalized.Type);
    }

    [Fact]
    public async Task UnmatchedIdentityPassesThroughAndWrongTypeFailsClosed()
    {
        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:device");
        var inner = new CapturingProcessor();
        var processor = new ExecutionNormalizationProcessor(inner, stream, "mtconnect", "configured-execution");
        var raw = Raw(stream, "ACTIVE");
        var otherAddress = new DurableMachineObservation(raw.Position, stream, raw.InstanceId, raw.Sequence,
            raw.Observation with { Address = "counter", Type = SignalType.Numeric, Value = 12m });
        var otherSource = new DurableMachineObservation(raw.Position, stream, raw.InstanceId, raw.Sequence,
            raw.Observation with { Source = "other" });
        await processor.ProcessAsync([otherAddress, otherSource]);
        Assert.Same(otherAddress, inner.Observations[0]);
        Assert.Same(otherSource, inner.Observations[1]);
        var wrongType = new DurableMachineObservation(raw.Position, stream, raw.InstanceId, raw.Sequence,
            raw.Observation with { Type = SignalType.Digital, Value = true });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await processor.ProcessAsync([wrongType]));
        var wrongStream = Raw(new ObservationStreamId(MachineId.New(), "mtconnect:device"), "ACTIVE");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.ProcessAsync([wrongStream]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await processor.ProcessAsync([raw], cancellation.Token));
    }

    [Fact]
    public async Task PerStreamConfigurationDrivesExistingAuthorityAndPreservesDurableRawOnReplay()
    {
        var first = new ObservationStreamId(MachineId.New(), "mtconnect:device-a");
        var second = new ObservationStreamId(MachineId.New(), "mtconnect:device-b");
        var values = BaseConfiguration();
        AddStream(values, 0, first, "real-execution-a");
        AddStream(values, 1, second, "different-execution-b");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(configuration, [first, second]);
        using var provider = services.BuildServiceProvider();
        var rawStore = provider.GetRequiredService<IObservationIngestionStore>();
        var pipelines = provider.GetRequiredService<DurableObservationProcessingPipelineSet>();
        var authority = provider.GetRequiredService<IMachineStateActivityAuthorityStore>();
        await rawStore.CommitAsync(Batch(first, "real-execution-a", "ACTIVE", 1));
        await rawStore.CommitAsync(Batch(second, "different-execution-b", "FEED_HOLD", 1));
        Assert.True(await pipelines.RunCycleAsync());
        Assert.Equal(MachineState.Running, (await authority.ReadAsync(new("machine-state-activity"), first))!.Projection.State);
        Assert.Equal(MachineState.Stopped, (await authority.ReadAsync(new("machine-state-activity"), second))!.Projection.State);
        await rawStore.CommitAsync(Batch(first, "real-execution-a", "NEW_ENUM", 2,
            new ObservationCheckpoint(first, 23, 2)));
        Assert.True(await pipelines.RunCycleAsync());
        Assert.Equal(MachineState.Unknown, (await authority.ReadAsync(new("machine-state-activity"), first))!.Projection.State);
        Assert.False(await pipelines.RunCycleAsync());
        var rawReader = provider.GetRequiredService<IDurableObservationReader>();
        var stored = await rawReader.ReadAsync(new ObservationReadRequest(first, null, 10));
        Assert.Equal(2, stored.Observations.Count);
        Assert.All(stored.Observations, item => Assert.Equal(SignalType.Enumeration, item.Observation.Type));
        Assert.Equal("ACTIVE", stored.Observations[0].Observation.Value);
        Assert.Equal("NEW_ENUM", stored.Observations[1].Observation.Value);
        Assert.All(stored.Observations, item => Assert.Equal(ObservationQuality.Good, item.Observation.Quality));
    }

    [Theory]
    [InlineData("ObservationProcessing:Mappings:0:Type", "Enumeration")]
    [InlineData("ObservationProcessing:Mappings:0:Invert", "true")]
    [InlineData("ObservationProcessing:Mappings:0:SignalKey", "state.idle")]
    [InlineData("ObservationProcessing:Mappings:0:Address", "other-address")]
    [InlineData("ObservationProcessing:ExecutionNormalization:Source", "")]
    [InlineData("DemoCanonicalInputs:Enabled", "true")]
    public void InvalidNormalizationConfigurationIsRejectedAtComposition(string key, string value)
    {
        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:device");
        var values = SingleConfiguration();
        values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        var exception = Record.Exception(() => services.AddFactoryConnectObservationProcessing(configuration, stream));
        Assert.NotNull(exception);
        Assert.True(exception is ArgumentException or InvalidOperationException);
    }

    [Fact]
    public void SingleStreamConfigurationComposesAndGlobalMultiStreamNormalizationIsRejected()
    {
        var stream = new ObservationStreamId(MachineId.New(), "mtconnect:device");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(SingleConfiguration()).Build();
        var services = new ServiceCollection();
        services.AddFactoryConnectObservationProcessing(configuration, stream);
        var values = BaseConfiguration();
        AddStream(values, 0, stream, "one");
        values["ObservationProcessing:ExecutionNormalization:Source"] = "mtconnect";
        values["ObservationProcessing:ExecutionNormalization:Address"] = "one";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddFactoryConnectObservationProcessing(configuration, [stream]));
    }

    private static Dictionary<string, string?> BaseConfiguration() => new()
    {
        ["Persistence:Provider"] = "InMemory",
        ["ObservationProcessing:BatchSize"] = "10",
        ["ObservationProcessing:PollingInterval"] = "00:00:01",
    };

    private static Dictionary<string, string?> SingleConfiguration()
    {
        var values = BaseConfiguration();
        AddMapping(values, "ObservationProcessing", "configured-execution");
        return values;
    }

    private static void AddStream(Dictionary<string, string?> values, int index, ObservationStreamId stream, string address)
    {
        var prefix = $"ObservationProcessing:Streams:{index}";
        values[$"{prefix}:MachineId"] = stream.MachineId.ToString();
        values[$"{prefix}:StreamKey"] = stream.StreamKey;
        AddMapping(values, prefix, address);
    }

    private static void AddMapping(Dictionary<string, string?> values, string prefix, string address)
    {
        values[$"{prefix}:ExecutionNormalization:Source"] = "mtconnect";
        values[$"{prefix}:ExecutionNormalization:Address"] = address;
        values[$"{prefix}:Mappings:0:Source"] = "mtconnect";
        values[$"{prefix}:Mappings:0:Address"] = address;
        values[$"{prefix}:Mappings:0:SignalKey"] = CanonicalSignalKeys.Running;
        values[$"{prefix}:Mappings:0:Type"] = "Digital";
        values[$"{prefix}:Mappings:0:Invert"] = "false";
    }

    private static DurableMachineObservation Raw(ObservationStreamId stream, string? value,
        ObservationQuality quality = ObservationQuality.Good) => new(new ObservationPosition(17), stream, 23, 41,
        new MachineObservation
        {
            MachineId = stream.MachineId, Source = "mtconnect", Address = "configured-execution",
            Type = SignalType.Enumeration, Value = value, Quality = quality,
            Timestamp = DateTimeOffset.UnixEpoch,
        });

    private static ObservationIngestionBatch Batch(ObservationStreamId stream, string address, string value,
        ulong sequence, ObservationCheckpoint? previous = null) => new(previous,
        new ObservationCheckpoint(stream, 23, sequence + 1),
        [new SequencedMachineObservation(sequence, Raw(stream, value).Observation with
        { Address = address, Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence) })],
        DateTimeOffset.UnixEpoch.AddSeconds(sequence));

    private sealed class CapturingProcessor : IObservationProcessor
    {
        public ObservationProcessorId ProcessorId { get; } = new("normalization-test");
        public IReadOnlyList<DurableMachineObservation> Observations { get; private set; } = [];
        public ValueTask ProcessAsync(IReadOnlyList<DurableMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            Observations = observations;
            return ValueTask.CompletedTask;
        }
    }
}
