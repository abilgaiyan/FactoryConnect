using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

internal static class ProductionContextReaderTestExtensions
{
    public static async Task<ProductionContextAssignment?> ResolveAsync(
        this IProductionContextReader reader,
        MachineId machineId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var result = await reader.ReadAsync(
            machineId,
            timestamp,
            timestamp.AddTicks(1),
            cancellationToken);
        return result.SingleOrDefault();
    }
}
