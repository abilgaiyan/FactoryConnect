using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Persistence.SqlServer;

internal static class ContainmentResultValidation
{
    public static void Validate(ContainmentResultDocument d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var definition = BuiltInOperationalMetricDefinitions.All.SingleOrDefault(definition => definition.Id == d.Key.DefinitionId)
            ?? throw new InvalidDataException("V2 supports only the fixed built-in definitions.");
        if (d.ProcessorId != AvailabilityContainmentV1.ProjectionProcessorId(d.Key.MachineId) ||
            d.SourceRevision.StreamId.MachineId != d.Key.MachineId || d.Unit != definition.ResultUnit ||
            d.Key.ContextKey != OperationalMetricEvaluationContextKey.Unpartitioned ||
            (d.Key.DefinitionId == BuiltInOperationalMetricDefinitions.PerformanceId) != (d.Authority is not null))
            throw new InvalidDataException("Invalid V2 processor, context, unit or historical authority binding.");
        if (!Enum.IsDefined(d.Status) || (d.ReasonCode is not null && !Enum.IsDefined(d.ReasonCode.Value)) ||
            (d.Status == OperationalMetricEvaluationStatus.Calculated
                ? d.Value is null || d.ReasonCode is not null || d.ReasonOperandName is not null
                : d.Value is not null || d.ReasonCode is null))
            throw new InvalidDataException("Invalid V2 status/value/reason shape.");
        if (d.Value is decimal value &&
            (value != decimal.Round(value, definition.PrecisionPolicy.DurableDecimalScale, definition.PrecisionPolicy.RoundingMode) ||
             (definition.DomainConstraints.MinimumInclusive is decimal min && value < min) ||
             (definition.DomainConstraints.MaximumInclusive is decimal max && value > max)))
            throw new InvalidDataException("V2 value violates its durable precision or domain.");

        var componentDefinitions = definition.Operands.Where(static o => o.Source is OperationalMetricOperandSource.Component).ToArray();
        var presentIndex = 0;
        foreach (var c in d.Components)
        {
            while (presentIndex < componentDefinitions.Length && componentDefinitions[presentIndex].OperandName != c.OperandName) presentIndex++;
            if (presentIndex == componentDefinitions.Length) throw new InvalidDataException("Unexpected, duplicated or reordered V2 component.");
            var expected = componentDefinitions[presentIndex++];
            var componentKey = ((OperationalMetricOperandSource.Component)expected.Source).ComponentKey;
            if (c.SourceIdentity.ComponentKey != OperationalMetricComponentKeyMapping.AggregateKey(componentKey) ||
                c.Dimension != expected.RequiredDimension || c.Unit != expected.RequiredUnit ||
                c.SourceRevision != d.SourceRevision || c.SourceIdentity.PeriodId != d.Key.PeriodId || c.SourceIdentity.MachineId != d.Key.MachineId ||
                c.FirstInputTimestamp.Offset != TimeSpan.Zero || c.LastInputTimestamp.Offset != TimeSpan.Zero)
                throw new InvalidDataException("V2 component contradicts its authored operand or source binding.");
        }
        if (d.Key.DefinitionId == BuiltInOperationalMetricDefinitions.OeeId)
        {
            if (d.Witness is not null || d.Dependencies.Count != definition.Operands.Count || d.Components.Count != 0)
                throw new InvalidDataException("OEE requires all fixed dependencies and no root witness.");
            for (var i = 0; i < definition.Operands.Count; i++)
            {
                var expected = definition.Operands[i]; var actual = d.Dependencies[i]; var child = actual.Result;
                if (actual.OperandName != expected.OperandName || actual.DefinitionId != ((OperationalMetricOperandSource.EvaluatedMetric)expected.Source).DefinitionId ||
                    child.Key.DefinitionId != actual.DefinitionId || child.ProcessorId != d.ProcessorId || child.SourceRevision != d.SourceRevision ||
                    child.Key.MachineId != d.Key.MachineId || child.Key.PeriodId != d.Key.PeriodId || child.Key.ContextKey != d.Key.ContextKey)
                    throw new InvalidDataException("V2 dependency is reordered or belongs to a foreign definition/cut.");
            }
            var failure = d.Dependencies.FirstOrDefault(static child => child.Result.Status != OperationalMetricEvaluationStatus.Calculated);
            if (failure is null)
            {
                Require(d, OperationalMetricEvaluationStatus.Calculated, null, null);
                // Fixed built-ins are a two-level tree. Recompute logical factors
                // from their complete decimal operands, not rounded child values.
                var logicalProduct = 1m;
                foreach (var dependency in d.Dependencies)
                {
                    var child = dependency.Result;
                    if (child.Components.Count != 2 || child.Components[1].Value == 0)
                        throw new InvalidDataException("Calculated OEE child lacks complete operands.");
                    logicalProduct *= child.Components[0].Value / child.Components[1].Value;
                }
                if (logicalProduct < 0 || logicalProduct > 1 || d.Value != decimal.Round(logicalProduct,
                        definition.PrecisionPolicy.DurableDecimalScale, definition.PrecisionPolicy.RoundingMode))
                    throw new InvalidDataException("V2 OEE contradicts its unrounded factors or domain.");
            }
            else Require(d, failure.Result.Status, failure.Result.Status == OperationalMetricEvaluationStatus.Unavailable
                ? OperationalMetricEvaluationReasonCode.DependencyUnavailable : OperationalMetricEvaluationReasonCode.DependencyInsufficientEvidence, failure.OperandName);
            // P3 must additionally reconstruct these operands from exact sources.
            return;
        }
        if (d.Dependencies.Count != 0) throw new InvalidDataException("Ratio nodes cannot contain dependency evidence.");
        var missing = componentDefinitions.FirstOrDefault(expected => !d.Components.Any(c => c.OperandName == expected.OperandName));
        if (missing is not null)
        {
            if (d.Witness is not null) throw new InvalidDataException("Missing operands cannot carry a witness.");
            Require(d, OperationalMetricEvaluationStatus.InsufficientEvidence,
                ((OperationalMetricOperandSource.Component)missing.Source).ComponentKey == MetricInputKeys.ProductionReferenceTime
                    ? OperationalMetricEvaluationReasonCode.MissingReferenceTime : OperationalMetricEvaluationReasonCode.MissingOperand, missing.OperandName);
            return;
        }
        var numerator = d.Components[0]; var denominator = d.Components[1];
        if (denominator.Value == 0)
        {
            if (d.Witness is not null) throw new InvalidDataException("Zero denominator cannot carry a witness.");
            Require(d, OperationalMetricEvaluationStatus.Unavailable, OperationalMetricEvaluationReasonCode.ZeroDenominator, denominator.OperandName);
            return;
        }
        if (d.Witness is not null)
        {
            OperationalMetricConflictWitnessV1Codec.Validate(d.Witness);
            if (d.Key.DefinitionId != BuiltInOperationalMetricDefinitions.AvailabilityId || d.Witness.SourceRevision != d.SourceRevision ||
                d.Witness.Period != d.Key.PeriodId || d.Witness.Numerator != numerator || d.Witness.Denominator != denominator)
                throw new InvalidDataException("Root witness contradicts its V2 result binding.");
            Require(d, OperationalMetricEvaluationStatus.Unavailable, OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, numerator.OperandName);
            return;
        }
        Require(d, OperationalMetricEvaluationStatus.Calculated, null, null);
        var logical = numerator.Value / denominator.Value;
        if ((definition.DomainConstraints.MinimumInclusive is decimal minimum && logical < minimum) ||
            (definition.DomainConstraints.MaximumInclusive is decimal maximum && logical > maximum) ||
            d.Value != decimal.Round(logical, definition.PrecisionPolicy.DurableDecimalScale, definition.PrecisionPolicy.RoundingMode))
            throw new InvalidDataException("V2 ratio contradicts its operands or domain.");
    }

    private static void Require(ContainmentResultDocument d, OperationalMetricEvaluationStatus status,
        OperationalMetricEvaluationReasonCode? reason, string? operand)
    {
        if (d.Status != status || d.ReasonCode != reason || d.ReasonOperandName != operand)
            throw new InvalidDataException("V2 outcome contradicts its evidence and failure precedence.");
    }
}
