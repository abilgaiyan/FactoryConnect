using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed class MtConnectAcquisitionRuntime :
    IMtConnectAcquisitionRuntime
{
    private MtConnectAcquisitionSession _session;
    private readonly MtConnectEndpoint _endpoint;
    private readonly MachineId _machineId;
    private readonly string _deviceKey;
    private readonly MtConnectTransientRetryPolicy _retryPolicy;
    private readonly MtConnectContinuityRecoveryPolicy _recoveryPolicy;
    private readonly IMtConnectObservationSink _sink;
    private readonly TimeSpan _pollingInterval;

    public MtConnectAcquisitionRuntime(
        MtConnectAcquisitionSession session,
        MtConnectEndpoint endpoint,
        MachineId machineId,
        string deviceKey,
        MtConnectTransientRetryPolicy retryPolicy,
        MtConnectContinuityRecoveryPolicy recoveryPolicy,
        IMtConnectObservationSink sink,
        TimeSpan pollingInterval)
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

        _session = session;
        _endpoint = endpoint;
        _machineId = machineId;
        _deviceKey = deviceKey;
        _retryPolicy = retryPolicy;
        _recoveryPolicy = recoveryPolicy;
        _sink = sink;
        _pollingInterval = pollingInterval;
    }

    public async Task<MtConnectSampleResult> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await AcquireWithRecoveryAsync(
            cancellationToken);

        await _sink.WriteAsync(result, cancellationToken);

        return result;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunCycleAsync(cancellationToken);

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
                        _session.AcquireNextAsync(
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
}
