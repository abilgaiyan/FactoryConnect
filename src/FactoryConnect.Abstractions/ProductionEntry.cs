namespace FactoryConnect.Abstractions;

public sealed record ProductionEntry
{
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required MachineId MachineId { get; init; }
    public required ShiftId ShiftId { get; init; }
    public required PartId PartId { get; init; }
    public OperatorId? OperatorId { get; init; }
    public string? JobReference { get; init; }
    public required DateOnly ProductionDate { get; init; }
    public required int ProducedQuantity { get; init; }
    public required int InProcessRejectedQuantity { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }

    public int GoodQuantity => ProducedQuantity - InProcessRejectedQuantity;
}
