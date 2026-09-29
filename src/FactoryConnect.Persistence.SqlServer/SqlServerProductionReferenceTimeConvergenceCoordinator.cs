using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Coordinates one exact FC-026 aggregation cut into a completed durable reference-time
/// publication transition. Completion is processor/A-wide: every produced source visible
/// through A is covered before the transition becomes authoritative for metric projection.
/// </summary>
public sealed class SqlServerProductionReferenceTimeConvergenceCoordinator
{
    private readonly string _connectionString;
    private readonly SqlServerMetricAggregationStore _aggregationStore;
    private readonly SqlServerProductionStandardAuthority _standardAuthority;
    private readonly SqlServerProductionReferenceTimePublicationTransitionStore _transitionStore;
    private readonly SqlServerProductionReferenceTimeTransitionOutcomeStore _outcomeStore;

    public SqlServerProductionReferenceTimeConvergenceCoordinator(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _aggregationStore = new SqlServerMetricAggregationStore(connectionString);
        _standardAuthority = new SqlServerProductionStandardAuthority(connectionString);
        _transitionStore = new SqlServerProductionReferenceTimePublicationTransitionStore(connectionString);
        _outcomeStore = new SqlServerProductionReferenceTimeTransitionOutcomeStore(connectionString);
    }

    public async Task<ProductionReferenceTimePublicationTransition> ConvergeAsync(
        MetricAggregationCheckpoint targetAggregationCheckpoint,
        MachineId machineId,
        MetricInputPosition? expectedPreviousCompletedAggregationPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetAggregationCheckpoint);

        // Transition authority is keyed by processor/A, so its source membership must be
        // the complete machine-stream inventory through A, not one shift/day projection.
        var transitionSources = await _aggregationStore.ReadProductionQuantityTransitionSourcesAsync(
            targetAggregationCheckpoint,
            machineId,
            cancellationToken);

        var transition = await _transitionStore.BeginAsync(
            targetAggregationCheckpoint.ProcessorId,
            targetAggregationCheckpoint.Position,
            expectedPreviousCompletedAggregationPosition,
            cancellationToken);
        if (transition.IsCompleted)
        {
            return transition;
        }

        var requiredSourceIds = transitionSources
            .Select(static source => source.Evidence.Id)
            .ToArray();
        var existingSourceIds = await ReadExistingSourceIdsAsync(
            targetAggregationCheckpoint.ProcessorId,
            requiredSourceIds,
            cancellationToken);
        var delta = transitionSources
            .Where(source => !existingSourceIds.Contains(source.Evidence.Id))
            .ToArray();

        if (delta.Length == 0)
        {
            var latest = await ReadLatestReferenceTimeRevisionAsync(
                targetAggregationCheckpoint.ProcessorId,
                cancellationToken);
            return await _transitionStore.CompleteAsync(
                targetAggregationCheckpoint.ProcessorId,
                targetAggregationCheckpoint.Position,
                latest,
                cancellationToken);
        }

        var standardCut = transition.ProductionStandardAuthorityRevision is long establishedStandardRevision
            ? await _standardAuthority.ReadCutAsync(establishedStandardRevision, cancellationToken)
            : await _standardAuthority.ReadCurrentCutAsync(cancellationToken);

        foreach (var source in delta)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = ProductionStandardResolver.Resolve(
                source.Evidence,
                source.ShiftOccurrenceId,
                source.ProductionDayId,
                standardCut);
            var latest = await ReadLatestReferenceTimeRevisionAsync(
                targetAggregationCheckpoint.ProcessorId,
                cancellationToken);
            var proposed = new PublishedProductionReferenceTimeOutcome(
                new ProductionReferenceTimeAuthorityRevision(checked(latest.Value + 1)),
                resolution);
            await _outcomeStore.AdmitAsync(
                targetAggregationCheckpoint.ProcessorId,
                targetAggregationCheckpoint.Position,
                proposed,
                cancellationToken);
        }

        // Re-read rather than trusting the loop. This is the completion gate for restart
        // and concurrent-worker cases: every produced source visible through A must have
        // one immutable durable outcome before A is marked completed.
        existingSourceIds = await ReadExistingSourceIdsAsync(
            targetAggregationCheckpoint.ProcessorId,
            requiredSourceIds,
            cancellationToken);
        if (requiredSourceIds.Any(sourceId => !existingSourceIds.Contains(sourceId)))
        {
            throw new InvalidOperationException(
                "Reference-time publication cannot complete because the target aggregation source cut is not fully covered.");
        }

        var completedRevision = await ReadLatestReferenceTimeRevisionAsync(
            targetAggregationCheckpoint.ProcessorId,
            cancellationToken);
        return await _transitionStore.CompleteAsync(
            targetAggregationCheckpoint.ProcessorId,
            targetAggregationCheckpoint.Position,
            completedRevision,
            cancellationToken);
    }

    private async Task<HashSet<ProductionQuantityEvidenceId>> ReadExistingSourceIdsAsync(
        MetricAggregationProcessorId processorId,
        IReadOnlyCollection<ProductionQuantityEvidenceId> sourceIds,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<ProductionQuantityEvidenceId>();
        if (sourceIds.Count == 0)
        {
            return result;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var sourceParameters = sourceIds.Select((sourceId, index) =>
        {
            var name = $"@Source{index}";
            command.Parameters.Add(name, SqlDbType.NVarChar, 256).Value = sourceId.Value;
            return name;
        }).ToArray();
        command.CommandText = $"""
            SELECT o.SourceQuantityEvidenceId
            FROM dbo.ProductionReferenceTimeOutcome o
            JOIN dbo.MetricAggregationProcessor p
              ON p.MetricAggregationProcessorRowId = o.MetricAggregationProcessorRowId
            WHERE p.ProcessorKey = @ProcessorKey
              AND o.SourceQuantityEvidenceId IN ({string.Join(",", sourceParameters)});
            """;
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorId.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProductionQuantityEvidenceId(reader.GetString(0)));
        }

        return result;
    }

    private async Task<ProductionReferenceTimeAuthorityRevision> ReadLatestReferenceTimeRevisionAsync(
        MetricAggregationProcessorId processorId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(r.ProductionReferenceTimeRevision)
            FROM dbo.ProductionReferenceTimeRevision r
            JOIN dbo.MetricAggregationProcessor p
              ON p.MetricAggregationProcessorRowId = r.MetricAggregationProcessorRowId
            WHERE p.ProcessorKey = @ProcessorKey;
            """;
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorId.Value;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is not decimal value)
        {
            throw new InvalidOperationException(
                "Reference-time authority is not initialized for the aggregation processor.");
        }

        return new ProductionReferenceTimeAuthorityRevision(checked((long)value));
    }
}
