using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Reuses only validated source inputs within one caller-owned Serializable transaction.
/// Callers must not write source facts, contribution membership or aggregation revisions in this scope.
/// Result evaluation and reference-time authority decisions are deliberately not cached.
/// </summary>
internal sealed class SqlContainmentHistoricalReadScope
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly EvidenceCache _inputs;

    internal SqlContainmentHistoricalReadScope(SqlConnection connection, SqlTransaction transaction)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        _connection = connection;
        _transaction = transaction;
        _inputs = new EvidenceCache((cut, token) =>
            SqlContainmentHistoricalContributionReader.ReadAsync(connection, transaction, cut, token));
    }

    internal void RequireOwner(SqlConnection connection, SqlTransaction transaction)
    {
        if (!ReferenceEquals(connection, _connection) || !ReferenceEquals(transaction, _transaction))
            throw new InvalidOperationException("Historical inputs cannot cross connection or transaction boundaries.");
        // Check on hits too: a completed/disposed transaction must not serve cached evidence.
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
    }

    internal Task<IReadOnlyList<PositionedMetricInputFact>> ReadAsync(MetricAggregationCheckpoint cut,
        CancellationToken cancellationToken)
    {
        RequireOwner(_connection, _transaction);
        return _inputs.ReadAsync(cut, cancellationToken);
    }

    // Database-free policy seam. The production loader above always runs the existing SQL reader
    // (binding, exact-revision, membership and fact integrity checks) before a value is accepted.
    internal sealed class EvidenceCache(
        Func<MetricAggregationCheckpoint, CancellationToken, Task<IReadOnlyList<PositionedMetricInputFact>>> load)
    {
        private readonly Dictionary<MetricAggregationCheckpoint, IReadOnlyList<PositionedMetricInputFact>> _cuts = [];
        private int _reading;

        internal async Task<IReadOnlyList<PositionedMetricInputFact>> ReadAsync(MetricAggregationCheckpoint cut,
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
                var inputs = await load(cut, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Own the collection; neither loader nor consumer can mutate cached membership.
                var frozen = Array.AsReadOnly(inputs.ToArray());
                cancellationToken.ThrowIfCancellationRequested();
                _cuts.Add(cut, frozen);
                return frozen;
            }
            finally { Volatile.Write(ref _reading, 0); }
        }
    }
}
