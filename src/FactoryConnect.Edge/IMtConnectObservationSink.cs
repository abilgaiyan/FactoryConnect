using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public interface IMtConnectObservationSink
{
    ValueTask WriteAsync(
        MtConnectSampleResult result,
        CancellationToken cancellationToken = default);
}
