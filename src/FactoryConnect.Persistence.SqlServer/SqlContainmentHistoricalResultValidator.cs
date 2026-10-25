using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Independently reconstructs fixed built-in results, including every OEE dependency, at exact historical authority.</summary>
internal static class SqlContainmentHistoricalResultValidator
{
    internal static async Task<ContainmentResultDocument> VerifyAsync(SqlConnection connection, SqlTransaction transaction,
        ReadOnlyMemory<byte> canonicalEnvelope, CancellationToken cancellationToken,
        SqlContainmentHistoricalReadScope? historicalReads = null) =>
        (await VerifyProjectionAsync(connection, transaction, canonicalEnvelope, cancellationToken, historicalReads)).Document;

    // Returns the Core-built projection only after independent reconstruction matches all canonical bytes.
    internal static async Task<(ContainmentResultDocument Document, OperationalMetricProjection Projection)> VerifyProjectionAsync(
        SqlConnection connection, SqlTransaction transaction, ReadOnlyMemory<byte> canonicalEnvelope,
        CancellationToken cancellationToken, SqlContainmentHistoricalReadScope? historicalReads = null)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        ContainmentResultDocument claim;
        try { claim = OperationalMetricDependencySnapshotV2Codec.Decode(canonicalEnvelope.Span); }
        catch (InvalidDataException error)
        { throw new ContainmentHistoricalIntegrityException("Malformed historical result envelope.", error); }
        // UnsupportedContainmentCodecException, SQL failures and cancellation retain their own identities.
        var reconstructed = await ReconstructVerifiedAsync(connection, transaction, claim.Key, claim.SourceRevision, cancellationToken, historicalReads);
        if (!OperationalMetricDependencySnapshotV2Codec.Encode(reconstructed.Document).AsSpan().SequenceEqual(canonicalEnvelope.Span))
            throw new ContainmentHistoricalIntegrityException("Result or recursive dependency evidence differs from independently reconstructed historical authority.");
        cancellationToken.ThrowIfCancellationRequested();
        return reconstructed;
    }

    internal static async Task<ContainmentResultDocument> ReconstructAsync(SqlConnection connection, SqlTransaction transaction,
        OperationalMetricEvaluationKey key, MetricAggregationCheckpoint cut, CancellationToken cancellationToken,
        SqlContainmentHistoricalReadScope? historicalReads = null) =>
        (await ReconstructVerifiedAsync(connection, transaction, key, cut, cancellationToken, historicalReads)).Document;

    // The projection contains Core-built witness proof, never a decoded wire witness.
    internal static async Task<(ContainmentResultDocument Document, OperationalMetricProjection Projection)> ReconstructVerifiedAsync(
        SqlConnection connection, SqlTransaction transaction, OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint cut, CancellationToken cancellationToken,
        SqlContainmentHistoricalReadScope? historicalReads = null)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(cut);
        if (key.MachineId != cut.StreamId.MachineId || key.ContextKey != OperationalMetricEvaluationContextKey.Unpartitioned ||
            !BuiltInOperationalMetricDefinitions.All.Any(d => d.Id == key.DefinitionId))
            throw new ArgumentException("Historical containment supports only exact unpartitioned built-in machine keys.", nameof(key));
        using var timing = OperationalMetricReportingDiagnostics.Source.StartActivity("historical.result.reconstruct");
        timing?.SetTag("processor.id", cut.ProcessorId.Value);
        timing?.SetTag("revision.position", cut.Position.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        historicalReads ??= new SqlContainmentHistoricalReadScope(connection, transaction);
        historicalReads.RequireOwner(connection, transaction);
        var reader = new TransactionSnapshotReader(connection, transaction, cut, historicalReads);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        try
        {
            // Core evaluates all OEE factors, even after an earlier factor is unavailable.
            var evaluation = await new OperationalMetricEvaluator(catalog, reader, cut.ProcessorId, true).EvaluateAsync(key, cancellationToken);
            var projection = new OperationalMetricProjectionFactory(catalog,
                AvailabilityContainmentV1.ProjectionProcessorId(key.MachineId)).Create(evaluation);
            var document = ContainmentResultDocument.FromProjection(projection,
                node => node.Key.DefinitionId == BuiltInOperationalMetricDefinitions.PerformanceId ? reader.ReferenceBinding : null);
            // Apply the same complete canonical semantic contract to independently reconstructed evidence.
            _ = OperationalMetricDependencySnapshotV2Codec.Encode(document);
            timing?.SetTag("stage.completed", true);
            return (document, projection);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or OverflowException or FormatException)
        { throw new ContainmentHistoricalIntegrityException("Historical contributions cannot reproduce the claimed built-in result.", error); }
    }

    private sealed class TransactionSnapshotReader(SqlConnection connection, SqlTransaction transaction,
        MetricAggregationCheckpoint cut, SqlContainmentHistoricalReadScope historicalReads) : IOperationalMetricComponentSnapshotReader
    {
        internal ContainmentHistoricalAuthority? ReferenceBinding { get; private set; }

        public async ValueTask<OperationalMetricComponentSnapshot> ReadAsync(OperationalMetricComponentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            var inputs = await historicalReads.ReadAsync(cut, cancellationToken);
            var aggregates = await historicalReads.ReadAggregatesAsync(cut, cancellationToken);
            var components = new List<OperationalMetricComponent>();
            foreach (var operand in request.Operands)
            {
                var sourceKey = ((OperationalMetricOperandSource.Component)operand.Source).ComponentKey;
                var aggregateKey = OperationalMetricComponentKeyMapping.AggregateKey(sourceKey);
                MetricAggregateValue? aggregate;
                if (sourceKey == MetricInputKeys.ProductionReferenceTime)
                {
                    var reference = await SqlContainmentReferenceTimeAuthorityValidator.ReadAsync(connection, transaction,
                        cut, request.EvaluationKey.PeriodId, cancellationToken, historicalReads);
                    ReferenceBinding = reference.Binding;
                    aggregate = reference.Aggregate;
                }
                else aggregate = request.EvaluationKey.PeriodId switch
                {
                    OperationalMetricPeriodId.Shift shift => aggregates.ShiftContributions.SingleOrDefault(c =>
                        c.Key == new ShiftMetricAggregateKey(request.EvaluationKey.MachineId, shift.ShiftOccurrenceId, aggregateKey))?.Value,
                    OperationalMetricPeriodId.ProductionDay day => aggregates.ProductionDayContributions.SingleOrDefault(c =>
                        c.Key == new ProductionDayMetricAggregateKey(request.EvaluationKey.MachineId, day.ProductionDayId, aggregateKey))?.Value,
                    _ => throw new InvalidDataException("Unsupported containment period.")
                };
                if (aggregate is not null)
                    components.Add(new OperationalMetricComponent(operand.OperandName,
                        new OperationalMetricAggregateSourceIdentity(cut.ProcessorId, request.EvaluationKey.MachineId,
                            request.EvaluationKey.PeriodId, aggregateKey), operand.RequiredDimension, aggregate));
            }
            return new OperationalMetricComponentSnapshot(request.EvaluationKey, cut, components,
                AvailabilityScopeConflictEvidenceBuilder.Build(request, cut, inputs, components));
        }
    }
}
