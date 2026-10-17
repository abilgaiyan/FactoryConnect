using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public interface IMtConnectObservationSink
{
    // Non-durable sinks may retain their original diagnostic behavior.
    ValueTask WriteWithEvidenceAsync(MtConnectSampleResult result, ObservationCheckpoint? expectedCheckpoint,
        DateTimeOffset successfulContactTime, AcquisitionResponseEvidence evidence, CancellationToken cancellationToken = default) =>
        WriteAsync(result, expectedCheckpoint, successfulContactTime, cancellationToken);

    ValueTask WriteAsync(
        MtConnectSampleResult result,
        ObservationCheckpoint? expectedCheckpoint,
        DateTimeOffset successfulContactTime,
        CancellationToken cancellationToken = default);
}
