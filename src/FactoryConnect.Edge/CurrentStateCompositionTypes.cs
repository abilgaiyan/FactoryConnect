using FactoryConnect.Abstractions;

namespace FactoryConnect.Edge;

public sealed class EdgeCurrentStateOwnerResolver : ICurrentStateOwnerResolver
{
    private static readonly ObservationProcessorId MappingProcessorId =
        new("canonical-mapping");
    private static readonly ObservationProcessorId StateProcessorId =
        new("machine-state-activity");

    private readonly MtConnectMachineInventory _inventory;

    public EdgeCurrentStateOwnerResolver(MtConnectMachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        _inventory = inventory;
    }

    public CurrentStateOwnerResolution Resolve(MachineId machineId)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        var matches = _inventory.ActivityStreams
            .Where(stream => stream.MachineId == machineId)
            .Take(2)
            .ToArray();

        return matches.Length switch
        {
            0 => CurrentStateNoOwner.Instance,
            1 => new CurrentStateExactlyOneOwner(
                new CurrentStateAuthorityBinding(
                    machineId,
                    matches[0],
                    MappingProcessorId,
                    StateProcessorId)),
            _ => CurrentStateAmbiguousOwner.Instance,
        };
    }
}

public sealed class EdgeCurrentStatePolicyResolver<TPolicy> :
    ICurrentStatePolicyResolver<TPolicy>
    where TPolicy : class
{
    private readonly TPolicy _policy;

    public EdgeCurrentStatePolicyResolver(TPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    public CurrentStatePolicyResolution<TPolicy> Resolve() =>
        new CurrentStateExactlyOnePolicy<TPolicy>(_policy);
}

public sealed class TimeProviderCurrentStateTimeProvider :
    ICurrentStateTimeProvider
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderCurrentStateTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();
}
