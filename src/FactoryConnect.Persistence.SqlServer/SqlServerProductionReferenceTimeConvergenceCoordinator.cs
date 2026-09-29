using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Coordinates one exact FC-026 aggregation cut into a completed durable reference-time
/// publication transition. Completion is processor/A-wide: every produced source visible
/// through the transition delta is covered before the transition becomes authoritative for metric projection.
/// </summary>
public sealed class SqlServerProductionReferenceTimeConvergenceCoordinator
{
    private readonly string _connectionString;
    private readonly SqlServerMetricAggregationStore _aggregationStore;
    private readonly SqlServerProductionStandardAuthority _standardAuthority;
    private readonly SqlServerProductionReferenceTimePublicationTransitionStore _transitionStore;
    private readonly SqlServerCanonicalProductionReferenceTimePublisher _publisher;

    public SqlServerProductionReferenceTimeConvergenceCoordinator(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _aggregationStore = new SqlServerMetricAggregationStore(connectionString);
        _standardAuthority = new SqlServerProductionStandardAuthority(connectionString);
        _transitionStore = new SqlServerProductionReferenceTimePublicationTransitionStore(connectionString);
        _publisher = new SqlServerCanonicalProductionReferenceTimePublisher(connectionString);
    }

    public async Task<ProductionReferenceTimePublicationTransition> ConvergeAsync(
        MetricAggregationCheckpoint targetAggregationCheckpoint,
        MachineId machineId,
        MetricInputPosition? expectedPreviousCompletedAggregationPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetAggregationCheckpoint);

        // Transition membership is the exact produced-source delta (Aprev, A].
        var transitionSources = await _aggregationStore.ReadProductionQuantityTransitionSourcesAsync(
            targetAggregationCheckpoint,
            machineId,
            expectedPreviousCompletedAggregationPosition,
            cancellationToken);

        var existingClaim = await _transitionStore.ReadAsync(
            targetAggregationCheckpoint.ProcessorId,
            targetAggregationCheckpoint.Position,
            cancellationToken);
        if (existingClaim is not null &&
            existingClaim.ExpectedPreviousAggregationPosition != expectedPreviousCompletedAggregationPosition)
        {
            throw new InvalidOperationException(
                "The requested predecessor does not match the durable reference-time transition claim.");
        }

        if (existingClaim is { IsCompleted: true })
        {
            return existingClaim;
        }

        ProductionStandardAuthorityCut? standardCut = null;
        ProductionReferenceTimePublicationTransition transition;
        if (existingClaim is not null)
        {
            transition = existingClaim;
            if (transition.ProductionStandardAuthorityRevision is long frozenStandard)
            {
                standardCut = await _standardAuthority.ReadCutAsync(frozenStandard, cancellationToken);
            }
        }
        else
        {
            var startingRevision = await ReadLatestReferenceTimeRevisionAsync(
                targetAggregationCheckpoint.ProcessorId,
                cancellationToken);
            if (transitionSources.Count != 0)
            {
                standardCut = await _standardAuthority.ReadCurrentCutAsync(cancellationToken);
            }

            try
            {
                transition = await _transitionStore.BeginAsync(
                    targetAggregationCheckpoint.ProcessorId,
                    targetAggregationCheckpoint.Position,
                    expectedPreviousCompletedAggregationPosition,
                    startingRevision,
                    standardCut?.Revision,
                    cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                transition = await ResumeCompetingClaimAsync(
                    targetAggregationCheckpoint, expectedPreviousCompletedAggregationPosition,
                    exception, cancellationToken);
            }
            catch (SqlException exception) when (exception.Number is 2601 or 2627 or 1205)
            {
                transition = await ResumeCompetingClaimAsync(
                    targetAggregationCheckpoint, expectedPreviousCompletedAggregationPosition,
                    exception, cancellationToken);
            }

            if (transition.ProductionStandardAuthorityRevision is long selected)
            {
                standardCut = standardCut is { Revision: var current } && current == selected
                    ? standardCut
                    : await _standardAuthority.ReadCutAsync(selected, cancellationToken);
            }
        }
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

        if (transitionSources.Count == 0)
        {
            return await _transitionStore.CompleteAsync(
                targetAggregationCheckpoint.ProcessorId,
                targetAggregationCheckpoint.Position,
                cancellationToken);
        }

        if (transition.ProductionStandardAuthorityRevision is not long establishedStandardRevision)
        {
            throw new InvalidOperationException(
                "A non-empty reference-time publication transition has no durable production-standard authority revision.");
        }

        if (standardCut is null || standardCut.Revision != establishedStandardRevision)
        {
            standardCut = await _standardAuthority.ReadCutAsync(establishedStandardRevision, cancellationToken);
        }

        foreach (var source in delta)
        {
            var resolution = ProductionStandardResolver.Resolve(
                source.Evidence,
                source.ShiftOccurrenceId,
                source.ProductionDayId,
                standardCut);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latest = await ReadLatestReferenceTimeRevisionAsync(
                    targetAggregationCheckpoint.ProcessorId,
                    cancellationToken);
                var proposed = new PublishedProductionReferenceTimeOutcome(
                    new ProductionReferenceTimeAuthorityRevision(checked(latest.Value + 1)),
                    resolution);
                try
                {
                    await _publisher.PublishAsync(
                        targetAggregationCheckpoint.ProcessorId,
                        targetAggregationCheckpoint.Position,
                        source.Evidence,
                        source.ShiftOccurrenceId,
                        source.ProductionDayId,
                        standardCut,
                        proposed,
                        cancellationToken);
                    break;
                }
                catch (ReferenceTimeRevisionRaceException)
                {
                    // Another claimant admitted a different source first; read R again.
                }
                catch (ReferenceTimeTransitionCompletedException)
                {
                    var completed = await _transitionStore.ReadAsync(
                        targetAggregationCheckpoint.ProcessorId,
                        targetAggregationCheckpoint.Position,
                        cancellationToken);
                    if (completed is { IsCompleted: true } &&
                        completed.ExpectedPreviousAggregationPosition == expectedPreviousCompletedAggregationPosition)
                    {
                        return completed;
                    }

                    throw;
                }
            }
        }

        // The store repeats exact set equality under the same serializable claim lock.
        return await _transitionStore.CompleteAsync(
            targetAggregationCheckpoint.ProcessorId,
            targetAggregationCheckpoint.Position,
            cancellationToken);
    }

    private async Task<ProductionReferenceTimePublicationTransition> ResumeCompetingClaimAsync(
        MetricAggregationCheckpoint checkpoint,
        MetricInputPosition? expectedPrevious,
        Exception cause,
        CancellationToken cancellationToken)
    {
        var transition = await _transitionStore.ReadAsync(
            checkpoint.ProcessorId, checkpoint.Position, cancellationToken)
            ?? throw new InvalidOperationException(
                "Reference-time claim creation failed without a durable competing claim.", cause);
        if (transition.ExpectedPreviousAggregationPosition != expectedPrevious)
        {
            throw new InvalidOperationException(
                "The competing reference-time transition claimed a different predecessor.");
        }

        return transition;
    }

    private async Task<HashSet<ProductionQuantityEvidenceId>> ReadExistingSourceIdsAsync(
        MetricAggregationProcessorId processorId,
        ProductionQuantityEvidenceId[] sourceIds,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<ProductionQuantityEvidenceId>();
        if (sourceIds.Length == 0)
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
