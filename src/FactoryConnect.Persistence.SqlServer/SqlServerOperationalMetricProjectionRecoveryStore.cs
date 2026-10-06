using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Retained-row repair only. Never invokes live commit, prerequisites, or checkpoint/manifest DML.</summary>
public sealed class SqlServerOperationalMetricProjectionRecoveryStore : IOperationalMetricProjectionRecoveryStore
{
    private readonly string _connectionString;
    // Internal test seam: observation/failure injection, not a production publication option.
    internal Func<int, CancellationToken, Task>? AfterInsert { get; init; }
    internal Func<CancellationToken, Task>? AfterGate { get; init; }
    public SqlServerOperationalMetricProjectionRecoveryStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask<OperationalMetricProjectionRecoveryResult> RecoverAsync(
        OperationalMetricProjectionRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var period = new OperationalMetricPeriodId.ProductionDay(request.ProductionDayId);
        // Historical read-only validation occurs outside the short publication transaction.
        var revision = await new SqlServerMetricAggregationStore(_connectionString)
            .ReadExactAsync(request.SourceRevision, cancellationToken);
        if (revision is null || !revision.ProductionDayIds.Contains(request.ProductionDayId))
            return new(OperationalMetricProjectionRecoveryOutcome.Conflict, 0);
        foreach (var projection in request.Projections)
        {
            foreach (var dependency in projection.DependencyEvidence)
            {
                var expected = request.Projections.Single(p => p.Key == dependency.Projection.Key);
                if (!OperationalMetricProjectionEquivalence.AreEquivalent(expected, dependency.Projection))
                    return new(OperationalMetricProjectionRecoveryOutcome.Conflict, 0);
            }
        }
        var models = request.Projections.Select(SqlServerOperationalMetricProjectionRows.Compile)
            .OrderBy(model => Convert.ToHexString(model.EvaluationKeyHash), StringComparer.Ordinal).ToArray();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await SqlServerOperationalMetricProjectionProcessorGate.AcquireAsync(connection, transaction,
                request.ProcessorId, OperationalMetricProjectionProcessorGateMode.Exclusive, cancellationToken);
            if (AfterGate is not null) await AfterGate(cancellationToken);
            var header = await SqlServerOperationalMetricProjectionCommitTransaction.ReadCheckpointHeaderAsync(
                connection, transaction, request.ProcessorId, cancellationToken);
            if (header is null || header.AggregationProcessorId != request.SourceRevision.ProcessorId ||
                header.StreamId != request.SourceRevision.StreamId || request.SourceRevision.Position > header.Position)
                return await ConflictAsync(transaction);
            var manifest = await SqlServerOperationalMetricProjectionSummaryReader.ReadSummariesAsync(
                connection, transaction, request.ProcessorId, true, cancellationToken);
            if (manifest.Any(p => p.Key.PeriodId == period)) return await ConflictAsync(transaction);
            var existing = await SqlServerOperationalMetricProjectionQueryReader.ReadProjectionsAsync(
                connection, transaction, request.ProcessorId, cancellationToken, period);
            var expectedKeys = request.Projections.Select(p => p.Key).ToHashSet();
            if (existing.Any(p => !expectedKeys.Contains(p.Key))) return await ConflictAsync(transaction);
            foreach (var projection in existing)
            {
                if (!OperationalMetricProjectionEquivalence.AreEquivalent(projection,
                    request.Projections.Single(p => p.Key == projection.Key))) return await ConflictAsync(transaction);
            }
            var existingKeys = existing.Select(p => p.Key).ToHashSet();
            var context = new SqlServerOperationalMetricProjectionCommitContext(connection, transaction,
                header.ProjectionProcessorRowId, SqlServerOperationalMetricProjectionCommitMode.Advance, header.Position);
            var inserted = 0;
            foreach (var model in models.Where(m => !existingKeys.Contains(m.Projection.Key)))
            {
                var id = await SqlServerOperationalMetricProjectionPublication.InsertProjectionAsync(context, model, cancellationToken);
                var row = new SqlServerOperationalMetricProjectionPublishedRow(id,
                    new SqlServerOperationalMetricProjectionPreparedRow(null, model));
                await SqlServerOperationalMetricProjectionPublication.InsertCompleteEvidenceAsync(context, [row], cancellationToken);
                inserted++;
                if (AfterInsert is not null) await AfterInsert(inserted, cancellationToken);
            }
            var actual = await SqlServerOperationalMetricProjectionQueryReader.ReadProjectionsAsync(
                connection, transaction, request.ProcessorId, cancellationToken, period);
            if (actual.Count != 5 || request.Projections.Any(p =>
                !actual.Any(a => OperationalMetricProjectionEquivalence.AreEquivalent(p, a))))
                throw new InvalidOperationException("Recovered period failed exact post-write validation.");
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            return new(inserted == 0 ? OperationalMetricProjectionRecoveryOutcome.Equivalent :
                OperationalMetricProjectionRecoveryOutcome.Recovered, inserted);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* Preserve primary operational failure. */ }
            throw;
        }
    }

    private static async Task<OperationalMetricProjectionRecoveryResult> ConflictAsync(SqlTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        return new(OperationalMetricProjectionRecoveryOutcome.Conflict, 0);
    }
}
