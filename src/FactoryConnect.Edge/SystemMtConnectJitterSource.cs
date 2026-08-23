namespace FactoryConnect.Edge;

public sealed class SystemMtConnectJitterSource :
    IMtConnectJitterSource
{
    public double NextDouble() => Random.Shared.NextDouble();
}
