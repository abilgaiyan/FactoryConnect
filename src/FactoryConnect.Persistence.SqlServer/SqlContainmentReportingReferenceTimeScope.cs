using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Successful period-specific reference-time authority decisions owned by one read-only reporting
/// transaction. Never supply this scope to publication preparation or a transaction that writes authority.
/// </summary>
internal sealed class SqlContainmentReportingReferenceTimeScope
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly Dictionary<Identity, SqlContainmentReferenceTimeAuthorityValidator.VerifiedReferenceTime> _validated = [];
    private int _reading;

    internal SqlContainmentReportingReferenceTimeScope(SqlConnection connection, SqlTransaction transaction)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        _connection = connection;
        _transaction = transaction;
    }

    internal int ReferenceTimeAuthorityValidationExecutions { get; private set; }
    internal int CacheHits { get; private set; }

    internal async Task<SqlContainmentReferenceTimeAuthorityValidator.VerifiedReferenceTime> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, MetricAggregationCheckpoint cut,
        OperationalMetricPeriodId period, CancellationToken cancellationToken,
        Func<CancellationToken, Task<SqlContainmentReferenceTimeAuthorityValidator.VerifiedReferenceTime>> validate)
    {
        if (!ReferenceEquals(connection, _connection) || !ReferenceEquals(transaction, _transaction))
            throw new InvalidOperationException("Reference-time authority cannot cross connection or transaction boundaries.");
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        ArgumentNullException.ThrowIfNull(cut);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(validate);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            throw new InvalidOperationException("Reference-time authority scopes require sequential use.");
        try
        {
            var identity = new Identity(cut, period, AvailabilityContainmentV1.PolicyId);
            if (_validated.TryGetValue(identity, out var cached))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CacheHits++;
                return cached;
            }
            ReferenceTimeAuthorityValidationExecutions++;
            var result = await validate(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
            // Null duration represents missing/unresolved reference-time evidence. Do not memoize it.
            // An unavailable OEE due to another factor does not invalidate a fully validated duration.
            if (result.Aggregate is not null) _validated.Add(identity, result);
            return result;
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private sealed record Identity(MetricAggregationCheckpoint Cut, OperationalMetricPeriodId Period, string Policy);
}
