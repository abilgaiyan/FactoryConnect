namespace FactoryConnect.Edge;

public interface IMtConnectContinuityReporter
{
    ValueTask ReportAsync(
        MtConnectContinuityLoss continuityLoss,
        CancellationToken cancellationToken = default);
}
