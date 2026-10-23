namespace FactoryConnect.Core;

/// <summary>
/// Classifies failures and waits before retrying a raw observation read.
/// This policy is never applied to processors or checkpoint operations.
/// </summary>
public interface IObservationReadRetryPolicy
{
    bool ShouldRetry(Exception exception);
    Task DelayAsync(Exception exception, CancellationToken cancellationToken);
}
