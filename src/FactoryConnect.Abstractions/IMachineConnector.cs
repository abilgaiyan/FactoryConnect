namespace FactoryConnect.Abstractions;

public interface IMachineConnector
{
    MachineId MachineId { get; }

    ValueTask<MachineSignalSnapshot> ReadSignalsAsync(
        CancellationToken cancellationToken = default);
}
