using System.Threading.Channels;
using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed class MtConnectAcquisitionRuntime :
    IMtConnectAcquisitionRuntime
{
    private readonly Channel<bool> _cycleGate = CreateCycleGate();
    private MtConnectAcquisitionSession _session;
    private readonly MtConnectEndpoint _endpoint;
    private readonly MachineId _machineId;
    private readonly string _deviceKey;
    private readonly MtConnectTransientRetryPolicy _retryPolicy;
    private readonly MtConnectContinuityRecoveryPolicy _recoveryPolicy;
    private readonly IMtConnectObservationSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollingInterval;
    private ObservationCheckpoint? _checkpoint;
    private PendingAcquisition? _pending;

    public MtConnectAcquisitionRuntime(
        MtConnectAcquisitionSession session,
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        MtConnectTransientRetryPolicy retryPolicy,
        MtConnectContinuityRecoveryPolicy recoveryPolicy,
        IMtConnectObservationSink sink,
        TimeProvider timeProvider,
        TimeSpan pollingInterval,
        ObservationCheckpoint? initialCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(recoveryPolicy);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (machineId.IsEmpty)
        {
            throw new ArgumentException(
                "Machine identifier must not be empty.",
                nameof(machineId));
        }

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingInterval),
                pollingInterval,
                "Polling interval must be greater than zero.");
        }

        var streamId = MtConnectObservationStreamId.Create(
            machineId,
            deviceKey);

        if (initialCheckpoint is not null &&
            initialCheckpoint.StreamId != streamId)
        {
            throw new ArgumentException(
                "Initial checkpoint must identify the runtime stream.",
                nameof(initialCheckpoint));
        }

        _session = session;
        _endpoint = endpoint;
        _machineId = machineId;
        _deviceKey = deviceKey;
        _retryPolicy = retryPolicy;
        _recoveryPolicy = recoveryPolicy;
        _sink = sink;
        _timeProvider = timeProvider;
        _pollingInterval = pollingInterval;
        _checkpoint = initialCheckpoint;
    }

    public async Task<MtConnectSampleResult> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var outcome = await RunCycleCoreAsync(
            containDurableWriteFailure: false,
            cancellationToken);

        return outcome.Result!;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleCoreAsync(
                    containDurableWriteFailure: true,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(
                    _pollingInterval,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<CycleOutcome> RunCycleCoreAsync(
        bool containDurableWriteFailure,
        CancellationToken cancellationToken)
    {
        await _cycleGate.Reader.ReadAsync(cancellationToken);
        try
        {
            if (_pending is null)
            {
                var result = await AcquireWithRecoveryAsync(
                    cancellationToken);
                var successfulContactTime = _timeProvider.GetUtcNow();
                _pending = new PendingAcquisition(
                    result,
                    successfulContactTime,
                    _checkpoint);
            }

            var pending = _pending;

            try
            {
                await _sink.WriteAsync(
                    pending.Result,
                    pending.ExpectedCheckpoint,
                    pending.SuccessfulContactTime,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (containDurableWriteFailure)
            {
                return CycleOutcome.DurableWriteFailed();
            }

            _session.Advance(pending.Result);
            _checkpoint = new ObservationCheckpoint(
                MtConnectObservationStreamId.Create(
                    _machineId,
                    _deviceKey),
                pending.Result.InstanceId,
                pending.Result.NextSequence);
            _pending = null;

            return CycleOutcome.Succeeded(pending.Result);
        }
        finally
        {
            _cycleGate.Writer.TryWrite(true);
        }
    }

    private async Task<MtConnectSampleResult>
        AcquireWithRecoveryAsync(
            CancellationToken cancellationToken)
    {
        var recoveryAttempted = false;

        while (true)
        {
            try
            {
                return await _retryPolicy.ExecuteAsync(
                    retryCancellationToken =>
                        _session.PrepareNextAsync(
                            _endpoint,
                            _machineId,
                            _deviceKey,
                            retryCancellationToken),
                    cancellationToken);
            }
            catch (MtConnectProtocolException exception)
                when (!recoveryAttempted &&
                      MtConnectContinuityRecoveryPolicy
                          .CanRecoverOutOfRange(exception))
            {
                _session =
                    await _recoveryPolicy.RecoverOutOfRangeAsync(
                        exception,
                        _session,
                        _endpoint,
                        _machineId,
                        _deviceKey,
                        cancellationToken);

                recoveryAttempted = true;
            }
            catch (MtConnectInstanceChangedException exception)
                when (!recoveryAttempted)
            {
                _session =
                    await _recoveryPolicy.RecoverInstanceChangeAsync(
                        exception,
                        _session,
                        _machineId,
                        cancellationToken);

                recoveryAttempted = true;
            }
        }
    }

    private static Channel<bool> CreateCycleGate()
    {
        var channel = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });

        if (!channel.Writer.TryWrite(true))
        {
            throw new InvalidOperationException(
                "The acquisition cycle gate could not be initialized.");
        }

        return channel;
    }

    private sealed record PendingAcquisition(
        MtConnectSampleResult Result,
        DateTimeOffset SuccessfulContactTime,
        ObservationCheckpoint? ExpectedCheckpoint);

    private sealed record CycleOutcome(
        MtConnectSampleResult? Result)
    {
        public static CycleOutcome Succeeded(
            MtConnectSampleResult result) =>
            new(result);

        public static CycleOutcome DurableWriteFailed() =>
            new((MtConnectSampleResult?)null);
    }
}
