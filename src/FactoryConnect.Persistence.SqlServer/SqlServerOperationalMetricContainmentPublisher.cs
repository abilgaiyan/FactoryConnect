using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricContainmentPublisher(string connectionString) : IOperationalMetricContainmentPublisher
{
    public async ValueTask PublishAsync(OperationalMetricProjectionCheckpoint? expectedCheckpoint,
        OperationalMetricProjectionCheckpoint proposedCheckpoint, IReadOnlyList<OperationalMetricProjection> projections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposedCheckpoint); ArgumentNullException.ThrowIfNull(projections);
        var frozen = projections.ToArray();
        var cut = proposedCheckpoint.SourceRevision;
        var processor = AvailabilityContainmentV1.ProjectionProcessorId(cut.StreamId.MachineId);
        if (proposedCheckpoint.ProcessorId != processor || frozen.Length == 0 ||
            frozen.Any(p => p is null || p.ProcessorId != processor || p.SourceRevision != cut || p.EvaluationPolicyId != AvailabilityContainmentV1.PolicyId) ||
            !proposedCheckpoint.BatchManifest.Equals(new OperationalMetricProjectionBatchManifest(frozen.Select(p => p.Key))))
            throw new ArgumentException("Containment batch must bind its exact processor, policy, cut and manifest.", nameof(projections));
        var envelopes = new List<ReadOnlyMemory<byte>>(frozen.Length);
        // Preparation is read-only; the atomic publisher independently revalidates every envelope
        // inside its own complete publication transaction. No proof from this transaction is trusted later.
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            foreach (var projection in frozen)
            {
                var verified = await SqlContainmentHistoricalResultValidator.ReconstructVerifiedAsync(connection, transaction,
                    projection.Key, cut, cancellationToken);
                if (!OperationalMetricProjectionEquivalence.AreEquivalent(projection, verified.Projection))
                    throw new ContainmentHistoricalIntegrityException("Runtime projection contradicts independently reconstructed historical evidence.");
                envelopes.Add(OperationalMetricDependencySnapshotV2Codec.Encode(verified.Document));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        await new SqlContainmentPublicationTransaction(connectionString).PublishAsync(envelopes, expectedCheckpoint, cancellationToken);
    }
}
