namespace FactoryConnect.Abstractions;

public interface IMachineConnector
{
    ValueTask<IReadOnlyDictionary<string, object?>> ReadSignalsAsync(
        CancellationToken cancellationToken = default);
}
