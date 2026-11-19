using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Reuses validated source inputs and their validated aggregate contribution set within one
/// caller-owned Serializable transaction and exact aggregation revision.
/// Callers must not write source facts, contribution membership or aggregation revisions in this scope.
/// Verified identical result envelopes can be reused within a reporting transaction. Reference-time reuse requires an explicit reporting-only scope.
/// </summary>
internal sealed class SqlContainmentHistoricalReadScope
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly EvidenceCache _inputs;

    internal SqlContainmentHistoricalReadScope(SqlConnection connection, SqlTransaction transaction,
        SqlContainmentReportingReferenceTimeScope? reportingReferenceTime = null)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        ReportingReferenceTime = reportingReferenceTime;
        _connection = connection;
        _transaction = transaction;
        _inputs = new EvidenceCache((cut, token) =>
            SqlContainmentHistoricalContributionReader.ReadValidatedAsync(connection, transaction, cut, token));
    }

    internal SqlContainmentReportingReferenceTimeScope? ReportingReferenceTime { get; }
    internal int RootReconstructionInvocations { get; private set; }
    private readonly Dictionary<string, (ContainmentResultDocument Document, OperationalMetricProjection Projection)> _verifiedResults = new(StringComparer.Ordinal);

    internal async Task<(ContainmentResultDocument Document, OperationalMetricProjection Projection)> VerifyReportingResultAsync(
        SqlConnection connection, SqlTransaction transaction, ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken)
    {
        RequireOwner(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReportingReferenceTime is null)
            return await SqlContainmentHistoricalResultValidator.VerifyProjectionAsync(
                connection, transaction, envelope, cancellationToken, this);

        // The complete canonical envelope is the key: a changed claim cannot reuse verification.
        // RequireOwner and cancellation are checked even on hits.
        var key = Convert.ToHexString(envelope.Span);
        if (_verifiedResults.TryGetValue(key, out var verified)) return verified;
        verified = await SqlContainmentHistoricalResultValidator.VerifyProjectionAsync(
            connection, transaction, envelope, cancellationToken, this);
        cancellationToken.ThrowIfCancellationRequested();
        _verifiedResults.Add(key, verified);
        return verified;
    }

    internal void RecordRootReconstruction()
    {
        RequireOwner(_connection, _transaction);
        RootReconstructionInvocations++;
    }

    internal void RequireOwner(SqlConnection connection, SqlTransaction transaction)
    {
        if (!ReferenceEquals(connection, _connection) || !ReferenceEquals(transaction, _transaction))
            throw new InvalidOperationException("Historical inputs cannot cross connection or transaction boundaries.");
        // Check on hits too: a completed/disposed transaction must not serve cached evidence.
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
    }

    internal async Task<IReadOnlyList<PositionedMetricInputFact>> ReadAsync(MetricAggregationCheckpoint cut,
        CancellationToken cancellationToken) =>
        (await ReadEvidenceAsync(cut, cancellationToken)).Inputs;

    internal async Task<MetricAggregateContributionSet> ReadAggregatesAsync(MetricAggregationCheckpoint cut,
        CancellationToken cancellationToken) =>
        (await ReadEvidenceAsync(cut, cancellationToken)).Aggregates;

    private Task<Evidence> ReadEvidenceAsync(MetricAggregationCheckpoint cut, CancellationToken cancellationToken)
    {
        RequireOwner(_connection, _transaction);
        return _inputs.ReadAsync(cut, cancellationToken);
    }

    internal sealed record Evidence(
        IReadOnlyList<PositionedMetricInputFact> Inputs,
        MetricAggregateContributionSet Aggregates);

    // Database-free policy seam. The production loader above always runs the existing SQL reader
    // (binding, exact-revision, membership and fact integrity checks) before a value is accepted.
    internal sealed class EvidenceCache(
        Func<MetricAggregationCheckpoint, CancellationToken,
            Task<SqlContainmentHistoricalContributionReader.ValidatedHistoricalContributions>> load)
    {
        private readonly Dictionary<MetricAggregationCheckpoint, Evidence> _cuts = [];
        private int _reading;

        internal async Task<Evidence> ReadAsync(MetricAggregationCheckpoint cut,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(cut);
            cancellationToken.ThrowIfCancellationRequested();
            // SqlConnection/SqlTransaction are used sequentially. Fail closed instead of permitting
            // overlapping loads or racing a cache hit against a load on the same transaction.
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
                throw new InvalidOperationException("Historical read scopes require sequential use.");
            try
            {
                if (_cuts.TryGetValue(cut, out var cached)) return cached;
                var validated = await load(cut, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Own input membership; aggregates were built from this exact validated membership.
                var frozen = new Evidence(Array.AsReadOnly(validated.Inputs.ToArray()), validated.Aggregates);
                cancellationToken.ThrowIfCancellationRequested();
                _cuts.Add(cut, frozen);
                return frozen;
            }
            finally { Volatile.Write(ref _reading, 0); }
        }
    }
}
