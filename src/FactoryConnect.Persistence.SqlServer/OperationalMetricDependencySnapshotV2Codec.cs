using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Canonical encoding/decoding only. Does not authorize publication.</summary>
internal static class OperationalMetricDependencySnapshotV2Codec
{
    public const ushort CodecVersion = 2;
    private const int MaximumCount = 65_535;
    private const int MaximumTotalEvidence = 262_144;

    public static byte[] Encode(ContainmentResultDocument document) => WriteNode(document, 1, new Budget());

    public static ContainmentResultDocument Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty || encoded.Length > OperationalMetricContainmentIdentity.MaximumResultLength)
            throw new InvalidDataException("Invalid FCDS V2 length.");
        try
        {
            var r = new ContainmentBinaryReader(encoded.ToArray());
            var result = ReadNode(r, 1, new Budget()); r.End();
            if (!Encode(result).AsSpan().SequenceEqual(encoded)) throw new InvalidDataException("Noncanonical FCDS V2 bytes.");
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or FormatException)
        { throw new InvalidDataException("Invalid FCDS V2 field.", exception); }
    }

    private static byte[] WriteNode(ContainmentResultDocument d, int depth, Budget budget)
    {
        CheckDepth(depth); ContainmentResultValidation.Validate(d);
        budget.Add(d.Components.Count); budget.Add(d.Dependencies.Count);
        using var w = new ContainmentBinaryWriter(OperationalMetricContainmentIdentity.MaximumResultLength);
        w.Bytes("FCDS"u8); w.U8(2); w.U16(CodecVersion); w.String(d.ProcessorId.Value);
        w.Blob(OperationalMetricEvaluationKeyV1Codec.Encode(d.Key), OperationalMetricEvaluationKeyV1Codec.MaximumEncodedLength);
        w.String(AvailabilityContainmentV1.PolicyId); w.U8((byte)d.Status); w.OptionalDecimal(d.Value); w.String(d.Unit);
        w.U8(d.ReasonCode is null ? (byte)0 : (byte)1); if (d.ReasonCode is not null) w.U8((byte)d.ReasonCode.Value);
        w.OptionalString(d.ReasonOperandName); ContainmentStructures.WriteCut(w, d.SourceRevision);
        WriteAuthority(w, d.Authority);
        w.U8(d.Witness is null ? (byte)0 : (byte)1);
        if (d.Witness is not null)
        {
            var bytes = OperationalMetricConflictWitnessV1Codec.Encode(d.Witness);
            w.Bytes(OperationalMetricContainmentIdentity.ComputeWitnessId(bytes));
            w.Blob(bytes, OperationalMetricContainmentIdentity.MaximumWitnessLength);
        }
        w.U32((uint)d.Components.Count);
        foreach (var component in d.Components) ContainmentStructures.WriteComponent(w, component);
        w.U32((uint)d.Dependencies.Count);
        foreach (var dependency in d.Dependencies)
        {
            w.String(dependency.OperandName); w.String(dependency.DefinitionId.MetricKey); w.String(dependency.DefinitionId.Version);
            w.Blob(WriteNode(dependency.Result, depth + 1, budget), OperationalMetricContainmentIdentity.MaximumResultLength);
        }
        return w.ToArray();
    }

    private static ContainmentResultDocument ReadNode(ContainmentBinaryReader r, int depth, Budget budget)
    {
        CheckDepth(depth); r.Require("FCDS"u8);
        if (r.U8() != 2) throw new InvalidDataException("Invalid FCDS kind.");
        var version = r.U16();
        if (version != CodecVersion) throw new UnsupportedContainmentCodecException("FCDS", version);
        var processor = new OperationalMetricProjectionProcessorId(r.String());
        var key = ContainmentStructures.ReadKey(r.Blob(OperationalMetricEvaluationKeyV1Codec.MaximumEncodedLength));
        if (r.String() != AvailabilityContainmentV1.PolicyId) throw new InvalidDataException("Invalid V2 policy.");
        var status = r.U8(); if (status > 2) throw new InvalidDataException("Invalid V2 status.");
        var value = r.OptionalDecimal(); var unit = r.String();
        OperationalMetricEvaluationReasonCode? reason = null;
        if (r.Present()) { var tag = r.U8(); if (tag > 6) throw new InvalidDataException("Invalid V2 reason."); reason = (OperationalMetricEvaluationReasonCode)tag; }
        var reasonOperand = r.OptionalString(); var cut = ContainmentStructures.ReadCut(r);
        var authority = ReadAuthority(r);
        ContainmentWitnessDocument? witness = null;
        if (r.Present())
        {
            var witnessId = r.Take(32); var bytes = r.Blob(OperationalMetricContainmentIdentity.MaximumWitnessLength);
            if (!OperationalMetricContainmentIdentity.ComputeWitnessId(bytes.Span).AsSpan().SequenceEqual(witnessId.Span))
                throw new InvalidDataException("Inline witness digest mismatch.");
            witness = OperationalMetricConflictWitnessV1Codec.Decode(bytes.Span);
        }
        var componentCount = r.Count(MaximumCount, 1); budget.Add(componentCount);
        var components = new OperationalMetricComponentProjectionEvidence[componentCount];
        for (var i = 0; i < componentCount; i++) components[i] = ContainmentStructures.ReadComponent(r);
        var dependencyCount = r.Count(MaximumCount, 1); budget.Add(dependencyCount);
        var dependencies = new ContainmentDependencyDocument[dependencyCount];
        for (var i = 0; i < dependencyCount; i++)
        {
            var name = r.String(); var definition = new OperationalMetricDefinitionId(r.String(), r.String());
            var childReader = new ContainmentBinaryReader(r.Blob(OperationalMetricContainmentIdentity.MaximumResultLength));
            var child = ReadNode(childReader, depth + 1, budget); childReader.End();
            dependencies[i] = new(name, definition, child);
        }
        var result = new ContainmentResultDocument(processor, key, (OperationalMetricEvaluationStatus)status, value, unit, reason,
            reasonOperand, cut, authority, witness, components, dependencies);
        ContainmentResultValidation.Validate(result); return result;
    }

    private static void WriteAuthority(ContainmentBinaryWriter w, ContainmentHistoricalAuthority? authority)
    {
        w.U8(authority is null ? (byte)0 : (byte)1);
        if (authority is null) return;
        w.U64((ulong)authority.CompletedReferenceRevision); w.U32((uint)authority.SourceQuantityIds.Count);
        foreach (var source in authority.SourceQuantityIds) w.String(source);
        w.U32((uint)authority.StandardRevisions.Count);
        foreach (var revision in authority.StandardRevisions) w.U64((ulong)revision);
    }
    private static ContainmentHistoricalAuthority? ReadAuthority(ContainmentBinaryReader r)
    {
        if (!r.Present()) return null;
        var revision = ReadRevision(r); var count = r.Count(MaximumCount, 5); var sources = new string[count];
        for (var i = 0; i < count; i++) sources[i] = r.String();
        count = r.Count(MaximumCount, 8); var standards = new long[count];
        for (var i = 0; i < count; i++) standards[i] = ReadRevision(r);
        return new(revision, sources, standards);
    }
    private static long ReadRevision(ContainmentBinaryReader r)
    {
        var value = r.U64(); if (value > long.MaxValue) throw new InvalidDataException("Historical revision exceeds Int64.");
        return (long)value;
    }
    private static void CheckDepth(int depth) { if (depth > 64) throw new InvalidDataException("Containment dependency depth exceeded."); }
    private sealed class Budget
    {
        private int _remaining = MaximumTotalEvidence;
        public void Add(int count)
        {
            if (count < 0 || count > MaximumCount || count > _remaining) throw new InvalidDataException("Containment evidence count exceeded.");
            _remaining -= count;
        }
    }
}
