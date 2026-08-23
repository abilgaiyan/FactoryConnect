namespace FactoryConnect.Edge;

public interface IMtConnectAcquisitionRuntime
{
    Task RunAsync(
        CancellationToken cancellationToken = default);
}
