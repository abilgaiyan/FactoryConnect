using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Fully validated cut-wide reference-time evidence owned by one read-only reporting
/// transaction. Never supply this scope to publication preparation or a transaction that writes authority.
/// </summary>
internal sealed class SqlContainmentReportingReferenceTimeScope
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly Dictionary<Identity, SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence> _validated = [];
    private int _reading;

    internal SqlContainmentReportingReferenceTimeScope(SqlConnection connection, SqlTransaction transaction)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        _connection = connection;
        _transaction = transaction;
    }

    internal int ReferenceTimeAuthorityValidationExecutions { get; private set; }
    internal int CacheHits { get; private set; }
    internal int PeriodEvaluations { get; private set; }

    internal void RecordPeriodEvaluation(SqlConnection connection, SqlTransaction transaction)
    {
        RequireOwner(connection, transaction);
        PeriodEvaluations++;
    }

    private void RequireOwner(SqlConnection connection, SqlTransaction transaction)
    {
        if (!ReferenceEquals(connection, _connection) || !ReferenceEquals(transaction, _transaction))
            throw new InvalidOperationException("Reference-time authority cannot cross connection or transaction boundaries.");
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
    }

    internal async Task<SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, MetricAggregationCheckpoint cut,
        Func<CancellationToken, Task<SqlContainmentReferenceTimeAuthorityValidator.ValidatedCutEvidence>> validate,
        CancellationToken cancellationToken)
    {
        RequireOwner(connection, transaction);
        ArgumentNullException.ThrowIfNull(cut);
        ArgumentNullException.ThrowIfNull(validate);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            throw new InvalidOperationException("Reference-time authority scopes require sequential use.");
        try
        {
            var identity = new Identity(cut, AvailabilityContainmentV1.PolicyId, SqlContainmentReferenceTimeAuthorityValidator.EvidenceContract);
            if (_validated.TryGetValue(identity, out var cached))
            {
                cancellationToken.ThrowIfCancellationRequested();
                cached.RequireOwner(connection, transaction, cut);
                CacheHits++;
                return cached;
            }
            ReferenceTimeAuthorityValidationExecutions++;
            var result = await validate(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireOwner(connection, transaction);
            result.RequireOwner(connection, transaction, cut);
            // Complete empty/unresolved authority evidence is valid; no period result is cached.
            _validated.Add(identity, result);
            return result;
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private sealed record Identity(MetricAggregationCheckpoint Cut, string Policy, string Contract);
}
