using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineStateEvaluatorTests
{
    [Fact]
    public void EvaluateReturnsFaultWhenFaultSignalIsActive()
    {
        var snapshot = CreateSnapshot(
            Signal(CanonicalSignalKeys.Running, true),
            Signal(CanonicalSignalKeys.Idle, true),
            Signal(CanonicalSignalKeys.Fault, true));

        var state = MachineStateEvaluator.Evaluate(snapshot);

        Assert.Equal(MachineState.Fault, state);
    }

    [Fact]
    public void EvaluateReturnsRunningWhenRunningSignalIsActive()
    {
        var snapshot = CreateSnapshot(
            Signal(CanonicalSignalKeys.Running, true),
            Signal(CanonicalSignalKeys.Idle, false),
            Signal(CanonicalSignalKeys.Fault, false));

        var state = MachineStateEvaluator.Evaluate(snapshot);

        Assert.Equal(MachineState.Running, state);
    }

    [Fact]
    public void EvaluateReturnsIdleWhenIdleSignalIsActive()
    {
        var snapshot = CreateSnapshot(
            Signal(CanonicalSignalKeys.Running, false),
            Signal(CanonicalSignalKeys.Idle, true),
            Signal(CanonicalSignalKeys.Fault, false));

        var state = MachineStateEvaluator.Evaluate(snapshot);

        Assert.Equal(MachineState.Idle, state);
    }

    [Fact]
    public void EvaluateReturnsStoppedWhenRunningSignalIsInactiveAndIdleIsInactive()
    {
        var snapshot = CreateSnapshot(
            Signal(CanonicalSignalKeys.Running, false),
            Signal(CanonicalSignalKeys.Idle, false),
            Signal(CanonicalSignalKeys.Fault, false));

        var state = MachineStateEvaluator.Evaluate(snapshot);

        Assert.Equal(MachineState.Stopped, state);
    }

    [Fact]
    public void EvaluateReturnsUnknownWhenRunningSignalIsUnavailable()
    {
        var snapshot = CreateSnapshot(
            Signal(CanonicalSignalKeys.Idle, false),
            Signal(CanonicalSignalKeys.Fault, false));

        var state = MachineStateEvaluator.Evaluate(snapshot);

        Assert.Equal(MachineState.Unknown, state);
    }

    [Fact]
    public void EvaluateIgnoresBadQualitySignals()
    {
        var snapshot = CreateSnapshot(
            Signal(CanonicalSignalKeys.Running, true, ObservationQuality.Bad));

        var state = MachineStateEvaluator.Evaluate(snapshot);

        Assert.Equal(MachineState.Unknown, state);
    }

    private static MachineSignalSnapshot CreateSnapshot(
        params MachineSignalValue[] signals) =>
        new(MachineId.New(), signals, DateTimeOffset.UtcNow);

    private static MachineSignalValue Signal(
        string key,
        bool value,
        ObservationQuality quality = ObservationQuality.Good) =>
        new()
        {
            Key = key,
            Type = SignalType.Digital,
            Value = value,
            Quality = quality,
        };
}
