namespace FactoryConnect.Edge;

public interface IMtConnectRetryDelay
{
    Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default);
}
