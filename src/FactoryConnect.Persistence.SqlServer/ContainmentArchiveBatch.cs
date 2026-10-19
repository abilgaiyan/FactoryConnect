using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class ContainmentPublicationConflictException(string message) : Exception(message);

/// <summary>Unverified preflight values. No archive receipt authorizes retained publication or checkpoint movement.</summary>
internal sealed class ContainmentArchiveBatch
{
    internal sealed record Reference(byte[] Path, byte[] WitnessId);
    internal sealed record Witness(byte[] Id, byte[] Payload, ContainmentWitnessDocument Document);
    internal sealed record Result(byte[] Id, byte[] Payload, byte[] Key, byte[] KeyHash,
        ContainmentResultDocument Document, IReadOnlyList<Reference> References);

    private ContainmentArchiveBatch(Result[] results, Witness[] witnesses)
    { Results = results; Witnesses = witnesses; }
    internal IReadOnlyList<Result> Results { get; }
    internal IReadOnlyList<Witness> Witnesses { get; }
    internal OperationalMetricProjectionProcessorId Processor => Results[0].Document.ProcessorId;
    internal MetricAggregationCheckpoint Cut => Results[0].Document.SourceRevision;

    internal static ContainmentArchiveBatch Preflight(IReadOnlyList<ReadOnlyMemory<byte>> envelopes)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        if (envelopes.Count is 0 or > 65535) throw new ArgumentException("An archive batch requires 1–65535 results.", nameof(envelopes));
        var results = new List<Result>();
        var witnesses = new Dictionary<string, Witness>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var resultIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in envelopes)
        {
            if (envelope.IsEmpty || envelope.Length > OperationalMetricContainmentIdentity.MaximumResultLength)
                throw new InvalidDataException("Archive result exceeds its canonical envelope bounds.");
            var payload = envelope.ToArray(); // Freeze caller-owned buffers before any database work.
            var document = OperationalMetricDependencySnapshotV2Codec.Decode(payload);
            if (results.Count != 0 && (document.ProcessorId != results[0].Document.ProcessorId || document.SourceRevision != results[0].Document.SourceRevision))
                throw new ArgumentException("All archive results must have one exact containment processor and source cut.", nameof(envelopes));
            var key = OperationalMetricEvaluationKeyV1Codec.Encode(document.Key);
            if (!keys.Add(Convert.ToHexString(key))) throw new ArgumentException("Duplicate archive evaluation key.", nameof(envelopes));
            var references = new List<Reference>();
            Walk(document, []);
            var resultId = OperationalMetricContainmentIdentity.ComputeResultId(payload);
            if (!resultIds.Add(Convert.ToHexString(resultId)))
                throw new ContainmentPublicationConflictException("Result digest collision inside incoming batch.");
            results.Add(new(resultId, payload, key,
                OperationalMetricEvaluationKeyV1Codec.ComputeHash(key), document, references.OrderBy(r => Convert.ToHexString(r.Path), StringComparer.Ordinal).ToArray()));

            void Walk(ContainmentResultDocument node, uint[] path)
            {
                if (node.Witness is { } witness)
                {
                    var bytes = OperationalMetricConflictWitnessV1Codec.Encode(witness);
                    var id = OperationalMetricContainmentIdentity.ComputeWitnessId(bytes);
                    var hex = Convert.ToHexString(id);
                    if (witnesses.TryGetValue(hex, out var existing) && !existing.Payload.AsSpan().SequenceEqual(bytes))
                        throw new ContainmentPublicationConflictException("Witness digest collision inside incoming batch.");
                    witnesses.TryAdd(hex, new(id, bytes, witness));
                    references.Add(new(OperationalMetricWitnessNodePathCodec.Encode(path), id));
                }
                for (var i = 0; i < node.Dependencies.Count; i++) Walk(node.Dependencies[i].Result, [.. path, (uint)i]);
            }
        }
        if (results.GroupBy(r => Convert.ToHexString(r.KeyHash)).Any(g => g.Count() > 1))
            throw new ContainmentPublicationConflictException("Evaluation-key digest collision inside incoming batch.");
        return new(results.OrderBy(r => Convert.ToHexString(r.KeyHash), StringComparer.Ordinal).ToArray(),
            witnesses.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray());
    }
}
