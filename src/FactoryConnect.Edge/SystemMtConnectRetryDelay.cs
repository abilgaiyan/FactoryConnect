namespace FactoryConnect.Edge;

public sealed class SystemMtConnectRetryDelay :
    IMtConnectRetryDelay
{
    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        return Task.Delay(delay, cancellationToken);
    }
}
