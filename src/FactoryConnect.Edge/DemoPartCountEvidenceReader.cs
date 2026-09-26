using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Edge;

/// <summary>
/// Reconstructs fixture counter deltas from durable raw observations. The demo explicitly
/// classifies each positive increment as good; this is not a production quality policy.
/// </summary>
public sealed class DemoPartCountEvidenceReader(
    IDurableObservationReader observations,
    IProductionContextReader contexts,
    ShiftOccurrenceResolver shifts,
    IReadOnlyDictionary<MachineId, ProductionContextProcessingScope> scopes)
    : IProductionQuantityEvidenceReader
{
    public async Task<IReadOnlyList<DurableProductionQuantityEvidence>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (!scopes.TryGetValue(streamId.MachineId, out var scope) ||
            !string.Equals(streamId.StreamKey, "part_count", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Demo quantity stream must be a configured part_count machine.");
        }

        // Reconstruct the predecessor on every read so a fresh process produces the
        // same increments after a persisted quantity checkpoint. Raw SQL is authoritative.
        ObservationPosition? cursor = null;
        decimal? previous = null;
        ulong? previousInstance = null;
        var result = new List<DurableProductionQuantityEvidence>();
        while (result.Count < batchSize)
        {
            var batch = await observations.ReadAsync(
                new ObservationReadRequest(scope.StreamId, cursor, 256), cancellationToken);
            if (batch.Observations.Count == 0)
            {
                break;
            }

            foreach (var item in batch.Observations)
            {
                cursor = item.Position;
                var source = item.Observation;
                if (!string.Equals(source.Source, "mtconnect", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(source.Address, "part_count", StringComparison.OrdinalIgnoreCase) ||
                    source.Type != SignalType.Numeric ||
                    source.Quality != ObservationQuality.Good)
                {
                    continue;
                }

                if (source.Value is not decimal count || count < 0 ||
                    count != decimal.Truncate(count) || count > int.MaxValue)
                {
                    throw new InvalidDataException("Demo PartCount must be a nonnegative whole-number counter.");
                }

                var delta = previousInstance == item.InstanceId && previous is not null &&
                    count >= previous.Value
                    ? count - previous.Value
                    : 0;
                previous = count;
                previousInstance = item.InstanceId;

                if (afterPosition is not null && item.Position <= afterPosition || delta == 0)
                {
                    continue;
                }

                var timestamp = source.Timestamp;
                var date = DateOnly.FromDateTime(timestamp.UtcDateTime);
                var occurrences = await shifts.ResolveAsync(
                    scope.SiteId, scope.ProductionLineId,
                    date.AddDays(-1), date.AddDays(2), cancellationToken);
                var matches = occurrences.Where(occurrence =>
                    timestamp >= occurrence.StartsAtUtc && timestamp < occurrence.EndsAtUtc).ToArray();
                var assignments = await contexts.ReadAsync(
                    scope.MachineId, timestamp, timestamp.AddTicks(1), cancellationToken);
                var currentContexts = assignments.Where(assignment => assignment.Contains(timestamp)).ToArray();
                if (matches.Length != 1 || currentContexts.Length != 1)
                {
                    throw new InvalidOperationException("Demo quantity must resolve to one shift and production context.");
                }

                var context = currentContexts[0];
                var evidence = new ProductionQuantityEvidence
                {
                    Id = new ProductionQuantityEvidenceId(
                        $"demo-part-count:{scope.MachineId}:{item.InstanceId}:{item.Sequence}:{item.Position.Value}"),
                    MachineId = scope.MachineId,
                    CompanyId = scope.CompanyId,
                    SiteId = scope.SiteId,
                    ProductionLineId = scope.ProductionLineId,
                    ShiftId = matches[0].ShiftId,
                    ProductionContextAssignmentId = context.Id,
                    ProductionOrderId = context.ProductionOrderId,
                    OperationId = context.OperationId,
                    PartId = context.PartId,
                    OperatorId = context.OperatorId,
                    OccurredAtUtc = timestamp,
                    PartCountIncrement = checked((int)delta),
                    GoodQuantity = checked((int)delta),
                };
                evidence.Validate();
                result.Add(new DurableProductionQuantityEvidence(item.Position, streamId, evidence));
                if (result.Count == batchSize)
                {
                    break;
                }
            }

            if (!batch.HasMore || result.Count == batchSize)
            {
                break;
            }
        }

        return result;
    }
}
