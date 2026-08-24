namespace FactoryConnect.Edge;

public interface IMtConnectAcquisitionRuntimeFactory
{
    ValueTask<IMtConnectAcquisitionRuntime> CreateAsync(
        CancellationToken cancellationToken = default);
}
