namespace FactoryConnect.Abstractions;

public sealed record ProductionContextAssignment
{
    public required ProductionContextAssignmentId Id { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required ProductionLineId ProductionLineId { get; init; }
    public required MachineId MachineId { get; init; }
    public ProductionOrderId? ProductionOrderId { get; init; }
    public OperationId? OperationId { get; init; }
    public PartId? PartId { get; init; }
    public OperatorId? OperatorId { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }

    public bool Contains(DateTimeOffset timestamp) =>
        timestamp >= EffectiveFrom &&
        (EffectiveTo is null || timestamp < EffectiveTo.Value);

    public bool Intersects(DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(to, from);

        return EffectiveFrom < to &&
            (EffectiveTo is null || EffectiveTo.Value > from);
    }

    public void Validate()
    {
        if (Id.IsEmpty)
        {
            throw new ArgumentException("Production context assignment ID is required.", nameof(Id));
        }

        if (CompanyId.IsEmpty)
        {
            throw new ArgumentException("Company ID is required.", nameof(CompanyId));
        }

        if (SiteId.IsEmpty)
        {
            throw new ArgumentException("Site ID is required.", nameof(SiteId));
        }

        if (ProductionLineId.IsEmpty)
        {
            throw new ArgumentException("Production line ID is required.", nameof(ProductionLineId));
        }

        if (MachineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(MachineId));
        }

        if (EffectiveTo is not null && EffectiveTo.Value <= EffectiveFrom)
        {
            throw new ArgumentException(
                "EffectiveTo must be greater than EffectiveFrom when specified.",
                nameof(EffectiveTo));
        }
    }
}
