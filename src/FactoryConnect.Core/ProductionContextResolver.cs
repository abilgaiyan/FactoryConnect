using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class ProductionContextResolver
{
    private readonly IProductionContextReader _reader;

    public ProductionContextResolver(IProductionContextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async Task<ProductionContextAssignment?> ResolveAtAsync(
        MachineId machineId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var assignments = await _reader.ReadAsync(
            machineId,
            timestamp,
            timestamp.AddTicks(1),
            cancellationToken).ConfigureAwait(false);

        ProductionContextAssignment? result = null;

        foreach (var assignment in assignments)
        {
            assignment.Validate();

            if (!assignment.Contains(timestamp))
            {
                continue;
            }

            if (result is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple production context assignments are effective for machine '{machineId}' at '{timestamp:O}'.");
            }

            result = assignment;
        }

        return result;
    }
}
