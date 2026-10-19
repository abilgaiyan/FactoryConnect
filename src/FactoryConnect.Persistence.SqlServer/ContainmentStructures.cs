using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

internal static class ContainmentStructures
{
    public static void WriteCut(ContainmentBinaryWriter w, MetricAggregationCheckpoint cut)
    {
        w.String(cut.ProcessorId.Value); w.Guid(cut.StreamId.MachineId.Value);
        w.String(cut.StreamId.StreamKey); w.U64(cut.Position.Value);
    }
    public static MetricAggregationCheckpoint ReadCut(ContainmentBinaryReader r) => new(
        new MetricAggregationProcessorId(r.String()), new MetricInputStreamId(new MachineId(r.Guid()), r.String()), new MetricInputPosition(r.U64()));

    public static void WriteShift(ContainmentBinaryWriter w, ShiftOccurrenceId shift)
    {
        w.String(shift.SiteId.Value); w.String(shift.ShiftScheduleAssignmentId.Value); w.String(shift.ShiftId.Value);
        w.Timestamp(shift.StartsAtUtc); w.Timestamp(shift.EndsAtUtc);
    }
    public static ShiftOccurrenceId ReadShift(ContainmentBinaryReader r) => new(
        new SiteId(r.String()), new ShiftScheduleAssignmentId(r.String()), new ShiftId(r.String()), r.Timestamp(), r.Timestamp());
    public static void WriteDay(ContainmentBinaryWriter w, ProductionDayId day) { w.String(day.SiteId.Value); w.I32(day.BusinessDate.DayNumber); }
    public static ProductionDayId ReadDay(ContainmentBinaryReader r) => new(new SiteId(r.String()), DateOnly.FromDayNumber(r.I32()));
    public static void WritePeriod(ContainmentBinaryWriter w, OperationalMetricPeriodId period)
    {
        switch (period)
        {
            case OperationalMetricPeriodId.Shift shift: w.U8(1); WriteShift(w, shift.ShiftOccurrenceId); break;
            case OperationalMetricPeriodId.ProductionDay day: w.U8(2); WriteDay(w, day.ProductionDayId); break;
            default: throw new InvalidDataException("Unsupported containment period.");
        }
    }
    public static OperationalMetricPeriodId ReadPeriod(ContainmentBinaryReader r) => r.U8() switch
    {
        1 => new OperationalMetricPeriodId.Shift(ReadShift(r)),
        2 => new OperationalMetricPeriodId.ProductionDay(ReadDay(r)),
        _ => throw new InvalidDataException("Unsupported containment period tag.")
    };

    public static void WriteComponent(ContainmentBinaryWriter w, OperationalMetricComponentProjectionEvidence component)
    {
        var source = component.SourceIdentity;
        w.String(component.OperandName); w.String(source.ProcessorId.Value); w.Guid(source.MachineId.Value);
        WritePeriod(w, source.PeriodId); w.String(source.ComponentKey); WriteCut(w, component.SourceRevision);
        w.U8((byte)component.Dimension); w.Decimal(component.Value); w.String(component.Unit); w.U64((ulong)component.InputCount);
        w.Timestamp(component.FirstInputTimestamp); w.Timestamp(component.LastInputTimestamp);
    }
    public static OperationalMetricComponentProjectionEvidence ReadComponent(ContainmentBinaryReader r)
    {
        var name = r.String(); var processor = new MetricAggregationProcessorId(r.String()); var machine = new MachineId(r.Guid());
        var period = ReadPeriod(r); var key = r.String(); var cut = ReadCut(r); var dimension = r.U8();
        if (dimension > 2) throw new InvalidDataException("Invalid containment component dimension.");
        var value = r.Decimal(); var unit = r.String(); var count = r.U64();
        if (count is 0 or > long.MaxValue) throw new InvalidDataException("Invalid containment input count.");
        return new(name, new OperationalMetricAggregateSourceIdentity(processor, machine, period, key), cut,
            (MetricDimension)dimension, value, unit, (long)count, r.Timestamp(), r.Timestamp());
    }

    public static void WriteFact(ContainmentBinaryWriter w, PositionedMetricInputFact input)
    {
        var f = input.Fact;
        w.Guid(input.StreamId.MachineId.Value); w.String(input.StreamId.StreamKey); w.U64(input.Position.Value);
        w.String(f.Id.Value); w.String(f.Key); w.Decimal(f.Value); w.String(f.Unit); w.Timestamp(f.StartsAtUtc); w.Timestamp(f.EndsAtUtc);
        w.String(f.CompanyId.Value); w.String(f.SiteId.Value); w.OptionalString(f.ProductionLineId?.Value); w.Guid(f.MachineId.Value);
        w.String(f.ShiftId.Value); w.OptionalString(f.ShiftScheduleAssignmentId?.Value); w.OptionalString(f.ProductionContextAssignmentId?.Value);
        w.OptionalString(f.ProductionOrderId?.Value); w.OptionalString(f.OperationId?.Value); w.OptionalString(f.PartId?.Value); w.OptionalString(f.OperatorId?.Value);
        w.U8(f.IsPlannedProductionTime switch { null => 0, false => 1, true => 2 });
        w.OptionalString(f.PlannedProductionScheduleAssignmentId?.Value); w.OptionalString(f.SourceContextualizedActivityIntervalId?.Value);
        w.OptionalString(f.SourceEligibilityIntervalId?.Value); w.OptionalString(f.SourceQuantityEvidenceId?.Value);
        WriteShift(w, input.ShiftOccurrenceId); WriteDay(w, input.ProductionDayId);
    }
    public static PositionedMetricInputFact ReadFact(ContainmentBinaryReader r)
    {
        var stream = new MetricInputStreamId(new MachineId(r.Guid()), r.String()); var position = new MetricInputPosition(r.U64());
        var id = new MetricInputFactId(r.String()); var key = r.String(); var value = r.Decimal(); var unit = r.String();
        var start = r.Timestamp(); var end = r.Timestamp(); var company = new CompanyId(r.String()); var site = new SiteId(r.String());
        var line = r.OptionalString(); var machine = new MachineId(r.Guid()); var shift = new ShiftId(r.String());
        var assignment = r.OptionalString(); var context = r.OptionalString(); var order = r.OptionalString();
        var operation = r.OptionalString(); var part = r.OptionalString(); var operatorId = r.OptionalString();
        bool? planned = r.U8() switch { 0 => null, 1 => false, 2 => true, _ => throw new InvalidDataException("Invalid containment optional boolean.") };
        var plannedAssignment = r.OptionalString(); var activity = r.OptionalString(); var eligibility = r.OptionalString(); var quantity = r.OptionalString();
        var fact = new DurableMetricInputFact
        {
            Id = id, Key = key, Value = value, Unit = unit, StartsAtUtc = start, EndsAtUtc = end, CompanyId = company, SiteId = site,
            ProductionLineId = line is null ? null : new ProductionLineId(line), MachineId = machine, ShiftId = shift,
            ShiftScheduleAssignmentId = assignment is null ? null : new ShiftScheduleAssignmentId(assignment),
            ProductionContextAssignmentId = context is null ? null : new ProductionContextAssignmentId(context),
            ProductionOrderId = order is null ? null : new ProductionOrderId(order), OperationId = operation is null ? null : new OperationId(operation),
            PartId = part is null ? null : new PartId(part), OperatorId = operatorId is null ? null : new OperatorId(operatorId),
            IsPlannedProductionTime = planned, PlannedProductionScheduleAssignmentId = plannedAssignment is null ? null : new PlannedProductionScheduleAssignmentId(plannedAssignment),
            SourceContextualizedActivityIntervalId = activity is null ? null : new ContextualizedActivityIntervalId(activity),
            SourceEligibilityIntervalId = eligibility is null ? null : new ProductionTimeEligibilityIntervalId(eligibility),
            SourceQuantityEvidenceId = quantity is null ? null : new ProductionQuantityEvidenceId(quantity)
        };
        return new(stream, position, fact, ReadShift(r), ReadDay(r));
    }

    public static OperationalMetricEvaluationKey ReadKey(ReadOnlyMemory<byte> encoded)
    {
        var r = new ContainmentBinaryReader(encoded); r.Require("FCEK"u8);
        if (r.U8() != 1 || r.U16() != 1) throw new InvalidDataException("Invalid FCEK V1 header.");
        var machine = new MachineId(r.Guid()); var period = ReadPeriod(r);
        // V2 only admits the unpartitioned built-in grain.
        for (var i = 0; i < 4; i++) if (r.Present()) throw new InvalidDataException("Containment context partition is unsupported.");
        var key = new OperationalMetricEvaluationKey(machine, period, new OperationalMetricDefinitionId(r.String(), r.String()), new OperationalMetricEvaluationContextKey());
        r.End();
        if (!OperationalMetricEvaluationKeyV1Codec.Encode(key).AsSpan().SequenceEqual(encoded.Span)) throw new InvalidDataException("Noncanonical FCEK bytes.");
        return key;
    }
}
