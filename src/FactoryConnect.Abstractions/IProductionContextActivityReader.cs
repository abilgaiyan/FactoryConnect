namespace FactoryConnect.Abstractions;

public interface IProductionContextActivityReader
{
    Task<IReadOnlyList<DurableMachineActivityPeriod>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken);
}
