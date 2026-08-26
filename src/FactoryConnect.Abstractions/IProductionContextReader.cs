namespace FactoryConnect.Abstractions;

public interface IProductionContextReader
{
    Task<IReadOnlyList<ProductionContextAssignment>> ReadAsync(
        MachineId machineId,
        DateTimeOffset effectiveFrom,
        DateTimeOffset effectiveTo,
        CancellationToken cancellationToken);
}
