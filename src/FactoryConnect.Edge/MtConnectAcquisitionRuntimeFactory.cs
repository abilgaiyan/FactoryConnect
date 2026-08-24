namespace FactoryConnect.Edge;

public sealed class MtConnectAcquisitionRuntimeFactory(
    MtConnectAcquisitionOptions options,
    MtConnectStartupCheckpointResolver startupResolver,
    IMtConnectAcquisitionSessionFactory sessionFactory,
    MtConnectTransientRetryPolicy retryPolicy,
    MtConnectContinuityRecoveryPolicy recoveryPolicy,
    IMtConnectObservationSink sink)
    : IMtConnectAcquisitionRuntimeFactory
{
    public async ValueTask<IMtConnectAcquisitionRuntime> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        var startupState = await startupResolver.ResolveAsync(
            options,
            cancellationToken);

        var session = sessionFactory.Create(
            startupState.FromSequence);

        return new MtConnectAcquisitionRuntime(
            session,
            options.Endpoint,
            options.MachineId,
            options.DeviceKey,
            retryPolicy,
            recoveryPolicy,
            sink,
            options.PollingInterval,
            startupState.Checkpoint);
    }
}
