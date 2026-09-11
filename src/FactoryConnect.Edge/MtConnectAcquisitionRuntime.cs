using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed class MtConnectAcquisitionRuntime :
    IMtConnectAcquisitionRuntime
{
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private MtConnectAcquisitionSession _session;
    private readonly MtConnectEndpoint _endpoint;
    private readonly MachineId _machineId;
    private readonly string _deviceKey;
    private readonly MtConnectTransientRetryPolicy _retryPolicy;
    private readonly MtConnectContinuityRecoveryPolicy _recoveryPolicy;
    private readonly IMtConnectObservationSink _sink;
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
        TimeSpan pollingInterval,
        ObservationCheckpoint? initialCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(recoveryPolicy);
        ArgumentNullException.ThrowIfNull(sink);

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
        _pollingInterval = pollingInterval;
        _checkpoint = initialCheckpoint;
    }

    public async Task<MtConnectSampleResult> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_pending is null)
            {
                var result = await AcquireWithRecoveryAsync(
                    cancellationToken);
                _pending = new PendingAcquisition(
                    result,
                    _checkpoint);
            }

            var pending = _pending;

            await _sink.WriteAsync(
                pending.Result,
                pending.ExpectedCheckpoint,
                cancellationToken);

            _session.Advance(pending.Result);
            _checkpoint = new ObservationCheckpoint(
                MtConnectObservationStreamId.Create(
                    _machineId,
                    _deviceKey),
                pending.Result.InstanceId,
                pending.Result.NextSequence);
            _pending = null;

            return pending.Result;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch when (_pending is not null)
            {
                // The logical acquisition has already succeeded. Retain the
                // pending write inside this runtime and retry that exact
                // command instead of allowing the worker to reconstruct the
                // runtime and reacquire /sample.
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

    private sealed record PendingAcquisition(
        MtConnectSampleResult Result,
        ObservationCheckpoint? ExpectedCheckpoint);
}
