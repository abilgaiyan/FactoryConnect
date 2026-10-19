using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Availability-only authority seam. Does not verify Performance or FCDS result trees.</summary>
internal static class SqlContainmentAvailabilityAuthorityValidator
{
    internal static async Task<OperationalMetricComponentSnapshot> ReconstructAsync(
        SqlConnection connection, SqlTransaction transaction, MetricAggregationCheckpoint cut,
        OperationalMetricEvaluationKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(cut);
        if (key.DefinitionId != BuiltInOperationalMetricDefinitions.AvailabilityId ||
            key.ContextKey != OperationalMetricEvaluationContextKey.Unpartitioned || key.MachineId != cut.StreamId.MachineId)
            throw new ArgumentException("Availability reconstruction requires the exact unpartitioned machine key.", nameof(key));
        var inputs = await SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, cut, cancellationToken);
        try
        {
            var aggregates = MetricInputContributionAggregator.Aggregate(cut.StreamId, inputs);
            var definition = BuiltInOperationalMetricDefinitions.All.Single(d => d.Id == BuiltInOperationalMetricDefinitions.AvailabilityId);
            var request = new OperationalMetricComponentSnapshotRequest(key, cut.ProcessorId, definition.Operands, true);
            var components = new List<OperationalMetricComponent>();
            foreach (var operand in definition.Operands)
            {
                var aggregateKey = OperationalMetricComponentKeyMapping.AggregateKey(
                    ((OperationalMetricOperandSource.Component)operand.Source).ComponentKey);
                var aggregate = key.PeriodId switch
                {
                    OperationalMetricPeriodId.Shift shift => aggregates.ShiftContributions.SingleOrDefault(c =>
                        c.Key == new ShiftMetricAggregateKey(key.MachineId, shift.ShiftOccurrenceId, aggregateKey))?.Value,
                    OperationalMetricPeriodId.ProductionDay day => aggregates.ProductionDayContributions.SingleOrDefault(c =>
                        c.Key == new ProductionDayMetricAggregateKey(key.MachineId, day.ProductionDayId, aggregateKey))?.Value,
                    _ => throw new ArgumentException("Unsupported Availability period.", nameof(key))
                };
                if (aggregate is not null)
                    components.Add(new OperationalMetricComponent(operand.OperandName,
                        new OperationalMetricAggregateSourceIdentity(cut.ProcessorId, key.MachineId, key.PeriodId, aggregateKey),
                        operand.RequiredDimension, aggregate));
            }
            return new OperationalMetricComponentSnapshot(key, cut, components,
                AvailabilityScopeConflictEvidenceBuilder.Build(request, cut, inputs, components));
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or OverflowException)
        {
            throw new ContainmentHistoricalIntegrityException("Historical Availability contributions cannot reproduce a valid snapshot.", error);
        }
    }

    internal static async Task<AvailabilityScopeConflictEvidenceV1> VerifyWitnessAsync(
        SqlConnection connection, SqlTransaction transaction, ContainmentWitnessDocument claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        byte[] claimedBytes;
        try { claimedBytes = OperationalMetricConflictWitnessV1Codec.Encode(claim); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or OverflowException)
        { throw new ContainmentHistoricalIntegrityException("Invalid Availability witness claim.", error); }
        var key = new OperationalMetricEvaluationKey(claim.SourceRevision.StreamId.MachineId, claim.Period,
            BuiltInOperationalMetricDefinitions.AvailabilityId, OperationalMetricEvaluationContextKey.Unpartitioned);
        var snapshot = await ReconstructAsync(connection, transaction, claim.SourceRevision, key, cancellationToken);
        var verified = snapshot.AvailabilityScopeConflictEvidence;
        if (verified is null || !OperationalMetricConflictWitnessV1Codec.Encode(ContainmentWitnessDocument.FromEvidence(verified))
                .AsSpan().SequenceEqual(claimedBytes))
            throw new ContainmentHistoricalIntegrityException("Witness differs from the complete historical operands or lowest-position offending contribution.");
        return verified;
    }
}
