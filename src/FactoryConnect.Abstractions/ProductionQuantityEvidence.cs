namespace FactoryConnect.Abstractions;

public sealed record ProductionQuantityEvidence
{
    public required ProductionQuantityEvidenceId Id { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public ProductionLineId? ProductionLineId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public ProductionContextAssignmentId? ProductionContextAssignmentId { get; init; }
    public ProductionOrderId? ProductionOrderId { get; init; }
    public OperationId? OperationId { get; init; }
    public PartId? PartId { get; init; }
    public OperatorId? OperatorId { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public int? PartCountIncrement { get; init; }
    public int? GoodQuantity { get; init; }
    public int? RejectedQuantity { get; init; }

    public void Validate()
    {
        if (Id.IsEmpty)
        {
            throw new ArgumentException("Production quantity evidence ID is required.", nameof(Id));
        }

        if (CompanyId.IsEmpty)
        {
            throw new ArgumentException("Company ID is required.", nameof(CompanyId));
        }

        if (SiteId.IsEmpty)
        {
            throw new ArgumentException("Site ID is required.", nameof(SiteId));
        }

        if (ProductionLineId is { IsEmpty: true })
        {
            throw new ArgumentException("Production line ID cannot be empty when specified.", nameof(ProductionLineId));
        }

        if (MachineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(MachineId));
        }

        if (ShiftId.IsEmpty)
        {
            throw new ArgumentException("Shift ID is required.", nameof(ShiftId));
        }

        ValidateNonNegative(PartCountIncrement, nameof(PartCountIncrement));
        ValidateNonNegative(GoodQuantity, nameof(GoodQuantity));
        ValidateNonNegative(RejectedQuantity, nameof(RejectedQuantity));

        if (PartCountIncrement is null && GoodQuantity is null && RejectedQuantity is null)
        {
            throw new ArgumentException("At least one quantity value is required.");
        }
    }

    private static void ValidateNonNegative(int? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Quantity values cannot be negative.");
        }
    }
}
