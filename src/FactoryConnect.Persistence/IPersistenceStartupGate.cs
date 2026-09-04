namespace FactoryConnect.Persistence;

public interface IPersistenceStartupGate
{
    ValueTask EnsureReadyAsync(CancellationToken cancellationToken);
}
