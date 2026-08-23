using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed class MtConnectContinuityRecoveryPolicy
{
    private readonly IMtConnectAcquisitionSessionFactory _sessionFactory;
    private readonly MtConnectCurrentClient _currentClient;
    private readonly MtConnectTransientRetryPolicy _retryPolicy;
    private readonly IMtConnectContinuityReporter _reporter;

    public MtConnectContinuityRecoveryPolicy(
        IMtConnectAcquisitionSessionFactory sessionFactory,
        MtConnectCurrentClient currentClient,
        MtConnectTransientRetryPolicy retryPolicy,
        IMtConnectContinuityReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(currentClient);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(reporter);

        _sessionFactory = sessionFactory;
        _currentClient = currentClient;
        _retryPolicy = retryPolicy;
        _reporter = reporter;
    }

    public static bool CanRecoverOutOfRange(
        MtConnectProtocolException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.ErrorResult.Errors.Any(
            error => string.Equals(
                error.Code,
                "OUT_OF_RANGE",
                StringComparison.OrdinalIgnoreCase));
    }

    public async Task<MtConnectAcquisitionSession>
        RecoverOutOfRangeAsync(
            MtConnectProtocolException exception,
            MtConnectAcquisitionSession previousSession,
            MtConnectEndpoint endpoint,
            MachineId machineId,
            string deviceKey,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(previousSession);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        if (!CanRecoverOutOfRange(exception))
        {
            throw new ArgumentException(
                "The MTConnect protocol exception does not contain " +
                "an OUT_OF_RANGE error.",
                nameof(exception));
        }

        var current = await _retryPolicy.ExecuteAsync(
            retryCancellationToken =>
                _currentClient.AcquireResultAsync(
                    endpoint,
                    machineId,
                    deviceKey,
                    retryCancellationToken),
            cancellationToken);

        var continuityLoss = new MtConnectContinuityLoss
        {
            MachineId = machineId,
            Reason = MtConnectContinuityLossReason.OutOfRange,
            PreviousInstanceId = previousSession.InstanceId,
            CurrentInstanceId = current.InstanceId,
            PreviousSequence = previousSession.NextSequence,
            RecoverySequence = current.FirstSequence,
        };

        await _reporter.ReportAsync(
            continuityLoss,
            cancellationToken);

        return _sessionFactory.Create(
            current.FirstSequence);
    }

    public async Task<MtConnectAcquisitionSession>
        RecoverInstanceChangeAsync(
            MtConnectInstanceChangedException exception,
            MtConnectAcquisitionSession previousSession,
            MachineId machineId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(previousSession);

        var continuityLoss = new MtConnectContinuityLoss
        {
            MachineId = machineId,
            Reason = MtConnectContinuityLossReason.InstanceChanged,
            PreviousInstanceId = exception.PreviousInstanceId,
            CurrentInstanceId = exception.CurrentInstanceId,
            PreviousSequence = previousSession.NextSequence,
            RecoverySequence = exception.FirstSequence,
        };

        await _reporter.ReportAsync(
            continuityLoss,
            cancellationToken);

        return _sessionFactory.Create(
            exception.FirstSequence);
    }
}
