using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public interface IMtConnectObservationSink
{
    ValueTask WriteAsync(
        MtConnectSampleResult result,
        ObservationCheckpoint? expectedCheckpoint,
        CancellationToken cancellationToken = default);
}
