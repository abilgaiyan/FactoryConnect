using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Decoded claims, never a verified Stage-A membership proof.</summary>
internal sealed record ContainmentWitnessDocument(
    MetricAggregationCheckpoint SourceRevision,
    OperationalMetricPeriodId Period,
    OperationalMetricComponentProjectionEvidence Numerator,
    OperationalMetricComponentProjectionEvidence Denominator,
    PositionedMetricInputFact Contribution)
{
    public static ContainmentWitnessDocument FromEvidence(AvailabilityScopeConflictEvidenceV1 witness) => new(
        witness.SourceRevision, witness.Numerator.SourceIdentity.PeriodId,
        Component(witness.Numerator, witness.SourceRevision), Component(witness.Denominator, witness.SourceRevision), witness.Contribution);

    private static OperationalMetricComponentProjectionEvidence Component(OperationalMetricComponent c, MetricAggregationCheckpoint cut) =>
        new(c.OperandName, c.SourceIdentity, cut, c.Dimension, c.Aggregate.Value, c.Aggregate.Unit,
            c.Aggregate.InputCount, c.Aggregate.FirstInputTimestamp, c.Aggregate.LastInputTimestamp);
}

internal static class OperationalMetricConflictWitnessV1Codec
{
    public const ushort CodecVersion = 1;

    public static byte[] Encode(ContainmentWitnessDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        using var w = new ContainmentBinaryWriter(OperationalMetricContainmentIdentity.MaximumWitnessLength);
        w.Bytes("FCWE"u8); w.U8(1); w.U16(CodecVersion);
        w.String(AvailabilityContainmentV1.PolicyId); w.String(AvailabilityContainmentV1.PlannedProductionOperandContractId);
        ContainmentStructures.WriteCut(w, document.SourceRevision); ContainmentStructures.WritePeriod(w, document.Period);
        ContainmentStructures.WriteComponent(w, document.Numerator); ContainmentStructures.WriteComponent(w, document.Denominator);
        ContainmentStructures.WriteFact(w, document.Contribution);
        return w.ToArray();
    }

    public static ContainmentWitnessDocument Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty || encoded.Length > OperationalMetricContainmentIdentity.MaximumWitnessLength)
            throw new InvalidDataException("Invalid FCWE envelope length.");
        try
        {
            var r = new ContainmentBinaryReader(encoded.ToArray()); r.Require("FCWE"u8);
            if (r.U8() != 1) throw new InvalidDataException("Invalid FCWE envelope kind.");
            var version = r.U16();
            if (version != CodecVersion) throw new UnsupportedContainmentCodecException("FCWE", version);
            if (r.String() != AvailabilityContainmentV1.PolicyId || r.String() != AvailabilityContainmentV1.PlannedProductionOperandContractId)
                throw new InvalidDataException("Invalid FCWE rule or operand contract.");
            var result = new ContainmentWitnessDocument(ContainmentStructures.ReadCut(r), ContainmentStructures.ReadPeriod(r),
                ContainmentStructures.ReadComponent(r), ContainmentStructures.ReadComponent(r), ContainmentStructures.ReadFact(r));
            r.End();
            if (!Encode(result).AsSpan().SequenceEqual(encoded)) throw new InvalidDataException("Noncanonical FCWE envelope.");
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or FormatException)
        { throw new InvalidDataException("Invalid FCWE field.", exception); }
    }

    internal static void Validate(ContainmentWitnessDocument d)
    {
        var f = d.Contribution.Fact;
        ValidateOperand(d.Numerator, "ActualProductionTime", MetricInputFactKeys.RunningDuration);
        ValidateOperand(d.Denominator, "PlannedOperatingTime", MetricInputFactKeys.PlannedProductionDuration);
        var ownsPeriod = d.Period switch
        {
            OperationalMetricPeriodId.Shift shift => shift.ShiftOccurrenceId == d.Contribution.ShiftOccurrenceId,
            OperationalMetricPeriodId.ProductionDay day => day.ProductionDayId == d.Contribution.ProductionDayId,
            _ => false
        };
        if (!ownsPeriod || d.Contribution.StreamId != d.SourceRevision.StreamId || d.Contribution.Position > d.SourceRevision.Position ||
            f.Key != MetricInputFactKeys.RunningDuration || f.Unit != MetricInputFactUnits.Seconds || f.Value <= 0 ||
            f.StartsAtUtc.Offset != TimeSpan.Zero || f.EndsAtUtc.Offset != TimeSpan.Zero || f.EndsAtUtc <= f.StartsAtUtc ||
            f.Value != (decimal)(f.EndsAtUtc - f.StartsAtUtc).Ticks / TimeSpan.TicksPerSecond ||
            f.IsPlannedProductionTime != false || f.PlannedProductionScheduleAssignmentId is not null ||
            f.SourceEligibilityIntervalId is null or { IsEmpty: true } || f.SourceContextualizedActivityIntervalId is null or { IsEmpty: true } ||
            f.StartsAtUtc < d.Numerator.FirstInputTimestamp || f.EndsAtUtc > d.Numerator.LastInputTimestamp || f.Value > d.Numerator.Value)
            throw new InvalidDataException("FCWE contribution contradicts its bound witness.");

        void ValidateOperand(OperationalMetricComponentProjectionEvidence c, string name, string key)
        {
            if (c.OperandName != name || c.SourceIdentity.ComponentKey != key || c.SourceRevision != d.SourceRevision ||
                c.SourceIdentity.PeriodId != d.Period || c.SourceIdentity.MachineId != d.SourceRevision.StreamId.MachineId ||
                c.Dimension != MetricDimension.Duration || c.Unit != MetricInputFactUnits.Seconds || c.Value <= 0)
                throw new InvalidDataException("Invalid FCWE operand binding.");
        }
        // Complete contribution-set membership, aggregate equality and lowest-position
        // selection require P3's independent historical reconstruction, not decoding.
    }
}

internal sealed class UnsupportedContainmentCodecException(string envelope, ushort version)
    : NotSupportedException($"Unsupported {envelope} codec version {version}."), IOperationalMetricReportingReadFailure
{
    public ushort CodecVersion { get; } = version;
    public OperationalMetricReportingReadFailure ReportingFailure => OperationalMetricReportingReadFailure.UnsupportedCodec;
}
