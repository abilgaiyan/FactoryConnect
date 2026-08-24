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

        var session = startupState.Checkpoint is null
            ? sessionFactory.Create(startupState.FromSequence)
            : sessionFactory.Restore(
                startupState.Checkpoint.InstanceId,
                startupState.Checkpoint.NextSequence);

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
