using System.Data;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.HistoricalRecovery;

/// <summary>
/// Non-hosted read composition. Registration only constructs provider objects;
/// no startup gate, worker, prerequisite coordinator, or writer is invoked.
/// </summary>
public sealed class SqlHistoricalRecoveryReader : IHistoricalRecoveryReader, IReportingAuthorityReader, IDisposable
{
    private readonly string _connectionString;
    private readonly ServiceProvider _serviceProvider;
    private readonly PersistenceProviderServices _reads;

    public SqlHistoricalRecoveryReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "FactoryConnect.P0D.HistoricalRecovery",
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        _connectionString = builder.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionString"] = _connectionString }).Build();
        var services = new ServiceCollection();
        services.AddSqlServerPersistenceProvider(configuration);
        _serviceProvider = services.BuildServiceProvider();
        _reads = _serviceProvider.GetRequiredService<IPersistenceProviderRegistration>().Create(_serviceProvider);
    }

    public async Task<HistoricalCapture> ReadAsync(
        RecoveryTarget target,
        IReadOnlyList<OperationalMetricOperandDefinition> operands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(operands);
        var revision = await _reads.MetricAggregationRevisionReader!
            .ReadExactAsync(target.Revision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Exact historical aggregation revision is unavailable.");
        if (revision.Revision != target.Revision || !revision.ProductionDayIds.Contains(target.Day))
        {
            throw new InvalidDataException("Exact revision does not identify the requested Production Day.");
        }

        var membership = await ReadMembershipAsync(target, cancellationToken).ConfigureAwait(false);
        var facts = await ReadPrefixFactsAsync(target, cancellationToken).ConfigureAwait(false);
        var key = new OperationalMetricEvaluationKey(
            target.Revision.StreamId.MachineId, target.Period,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var snapshot = await _reads.RevisionedOperationalMetricComponentSnapshotReader!
            .ReadAtRevisionAsync(new OperationalMetricComponentSnapshotRequest(
                key, target.Revision.ProcessorId, operands), target.Revision, cancellationToken).ConfigureAwait(false);
        var transition = await new SqlServerProductionReferenceTimePublicationTransitionStore(_connectionString)
            .ReadAsync(target.Revision.ProcessorId, target.Revision.Position, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Historical reference-time transition is unavailable.");
        if (!transition.IsCompleted || transition.CompletedReferenceTimeRevision is not { } completedRevision)
        {
            throw new InvalidDataException("Historical reference-time transition is not completed.");
        }

        // ReadAtRevision validates that the exact reference-time revision exists, including revision zero.
        var reference = await new SqlReferenceTimeReadCapture(_connectionString)
            .ReadAsync(target.Revision.ProcessorId,
                completedRevision, cancellationToken).ConfigureAwait(false);
        var quantity = await ((IProductionQuantitySourceCutReader)_reads.MetricAggregationStore)
            .ReadProductionQuantitySourceCutAsync(target.Revision, target.Revision.StreamId.MachineId,
                target.Period, cancellationToken).ConfigureAwait(false);
        var standard = transition.ProductionStandardAuthorityRevision is long standardRevision
            ? await new SqlServerProductionStandardAuthority(_connectionString)
                .ReadCutAsync(standardRevision, cancellationToken).ConfigureAwait(false)
            : null;
        var live = await ReadLiveAuthorityAsync(target, cancellationToken).ConfigureAwait(false);
        return new HistoricalCapture(revision, membership, facts, snapshot, transition,
            quantity, reference, standard, live);
    }

    private async Task<IReadOnlyList<ContributionIdentity>> ReadMembershipAsync(
        RecoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.MetricInputFactRowId, c.Position, f.FactId,
                   c.MetricInputStreamRowId, f.MetricInputStreamRowId, f.Position,
                   p.MetricInputStreamRowId
            FROM dbo.MetricAggregationContribution c
            JOIN dbo.MetricAggregationProcessor p
              ON p.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
            JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId = p.MetricInputStreamRowId
            LEFT JOIN dbo.MetricInputFact f ON f.MetricInputFactRowId = c.MetricInputFactRowId
            WHERE p.ProcessorKey = @Processor AND s.MachineId = @Machine
              AND s.StreamKey = @Stream AND c.Position <= @Revision
            ORDER BY c.Position;
            """;
        Bind(command, target);
        var result = new List<ContributionIdentity>();
        await using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.IsDBNull(2) || rows.IsDBNull(4) || rows.IsDBNull(5) ||
                rows.GetInt64(3) != rows.GetInt64(4) || rows.GetInt64(3) != rows.GetInt64(6) ||
                rows.GetDecimal(1) != rows.GetDecimal(5))
            {
                throw new InvalidDataException("Contribution is orphaned or differs from its exact fact stream/position.");
            }

            result.Add(new ContributionIdentity(
                rows.GetInt64(0), checked((ulong)rows.GetDecimal(1)), rows.GetString(2)));
        }

        return result;
    }

    private async Task<IReadOnlyList<PositionedMetricInputFact>> ReadPrefixFactsAsync(
        RecoveryTarget target, CancellationToken cancellationToken)
    {
        var facts = new List<PositionedMetricInputFact>();
        MetricInputPosition? cursor = null;
        while (true)
        {
            var batch = await _reads.MetricInputReader.ReadAsync(
                new MetricInputReadRequest(target.Revision.StreamId, cursor, 1024), cancellationToken)
                .ConfigureAwait(false);
            if (batch.Facts.Count == 0)
            {
                break;
            }

            foreach (var fact in batch.Facts)
            {
                if (fact.Position > target.Revision.Position)
                {
                    return facts;
                }

                facts.Add(fact);
            }

            var next = batch.Facts[^1].Position;
            if (cursor is not null && next <= cursor)
            {
                throw new InvalidDataException("Metric-input reader did not progress.");
            }

            cursor = next;
            if (cursor >= target.Revision.Position)
            {
                break;
            }
        }

        return facts;
    }

    private async Task<JsonElement> ReadLiveAuthorityAsync(RecoveryTarget target, CancellationToken cancellationToken)
    {
        var authority = await ReadReportingAuthorityAsync(target, cancellationToken).ConfigureAwait(false);
        return RecoveryJson.Element(authority);
    }

    public async Task<ReportingAuthority> ReadReportingAuthorityAsync(RecoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await SqlServerOperationalMetricProjectionStableRead.ExecuteAsync(connection, RecoveryTargets.Processor,
            async (c, transaction, token) =>
            {
                var header = await SqlServerOperationalMetricProjectionCommitTransaction.ReadCheckpointHeaderAsync(
                    c, transaction, RecoveryTargets.Processor, token).ConfigureAwait(false);
                var manifest = await SqlServerOperationalMetricProjectionSummaryReader.ReadSummariesAsync(
                    c, transaction, RecoveryTargets.Processor, true, token).ConfigureAwait(false);
                var retained = await SqlServerOperationalMetricProjectionQueryReader.ReadProjectionsAsync(
                    c, transaction, RecoveryTargets.Processor, token).ConfigureAwait(false);
                return new ReportingAuthority(RecoveryTargets.Processor,
                    header is null ? null : new MetricAggregationCheckpoint(header.AggregationProcessorId, header.StreamId, header.Position),
                    manifest.Select(value => value.Key).ToArray(), retained);
            }, cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqlCommand command, RecoveryTarget target)
    {
        command.Parameters.Add("@Processor", SqlDbType.NVarChar, 256).Value = target.Revision.ProcessorId.Value;
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = target.Revision.StreamId.MachineId.Value;
        command.Parameters.Add("@Stream", SqlDbType.NVarChar, 256).Value = target.Revision.StreamId.StreamKey;
        var revision = command.Parameters.Add("@Revision", SqlDbType.Decimal);
        revision.Precision = 20;
        revision.Scale = 0;
        revision.Value = checked((decimal)target.Revision.Position.Value);
    }

    public void Dispose() => _serviceProvider.Dispose();
}
