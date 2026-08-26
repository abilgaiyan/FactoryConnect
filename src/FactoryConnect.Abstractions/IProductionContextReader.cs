namespace FactoryConnect.Abstractions;

public interface IProductionContextReader
{
    Task<IReadOnlyList<ProductionContextAssignment>> ReadAsync(
        MachineId machineId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}
