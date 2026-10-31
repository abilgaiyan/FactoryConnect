using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Reconstructs exact A/R/S authority. Every read belongs to the caller's Serializable transaction.</summary>
internal static partial class SqlContainmentReferenceTimeAuthorityValidator
{
    internal sealed record VerifiedReferenceTime(ContainmentHistoricalAuthority Binding, MetricAggregateValue? Aggregate);

    internal const string EvidenceContract = "reference-time-cut-evidence-v1";

    internal static async Task<VerifiedReferenceTime> ReadAsync(SqlConnection connection, SqlTransaction transaction,
        MetricAggregationCheckpoint cut, OperationalMetricPeriodId period,
        CancellationToken cancellationToken, SqlContainmentHistoricalReadScope? historicalReads = null)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        historicalReads ??= new SqlContainmentHistoricalReadScope(connection, transaction);
        historicalReads.RequireOwner(connection, transaction);
        ValidatedCutEvidence evidence;
        if (historicalReads.ReportingReferenceTime is { } reporting)
            evidence = await reporting.ReadAsync(connection, transaction, cut,
                token => ValidatedCutEvidence.LoadAsync(connection, transaction, cut, historicalReads, token), cancellationToken);
        else
            evidence = await ValidatedCutEvidence.LoadAsync(connection, transaction, cut, historicalReads, cancellationToken);
        historicalReads.RequireOwner(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        historicalReads.ReportingReferenceTime?.RecordPeriodEvaluation(connection, transaction);
        return evidence.EvaluatePeriod(connection, transaction, period, cancellationToken);
    }

    // Only the full phase-A loader can construct this proof. It accepts NO requested period.
    // Collections remain private and own all nested collection membership.
    internal sealed class ValidatedCutEvidence
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;
        private readonly long _reference;
        private readonly IReadOnlyList<ProductionQuantitySourceEntry> _sources;
        private readonly IReadOnlyList<ProductionReferenceTimeResolution> _outcomes;

        private ValidatedCutEvidence(SqlConnection connection, SqlTransaction transaction,
            MetricAggregationCheckpoint cut, long reference, IEnumerable<ProductionQuantitySourceEntry> sources,
            IEnumerable<PublishedProductionReferenceTimeOutcome> outcomes)
        {
            _connection = connection;
            _transaction = transaction;
            Cut = cut;
            _reference = reference;
            _sources = Array.AsReadOnly(sources.Select(source => source with
            {
                ContributingPositions = Array.AsReadOnly(source.ContributingPositions.ToArray()),
            }).ToArray());
            _outcomes = Array.AsReadOnly(outcomes.Select(published => published.Resolution with
            {
                ConflictingStandardVersionIds = Array.AsReadOnly(published.Resolution.ConflictingStandardVersionIds.ToArray()),
            }).ToArray());
        }

        internal MetricAggregationCheckpoint Cut { get; }

        internal void RequireOwner(SqlConnection connection, SqlTransaction transaction, MetricAggregationCheckpoint cut)
        {
            if (!ReferenceEquals(connection, _connection) || !ReferenceEquals(transaction, _transaction) || Cut != cut)
                throw new InvalidOperationException("Validated reference-time evidence cannot cross owner or exact-cut boundaries.");
            SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        }

        internal static async Task<ValidatedCutEvidence> LoadAsync(SqlConnection connection, SqlTransaction transaction,
            MetricAggregationCheckpoint cut, SqlContainmentHistoricalReadScope historicalReads, CancellationToken cancellationToken)
        {
            historicalReads.RequireOwner(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            using var timing = OperationalMetricReportingDiagnostics.Source.StartActivity("historical.reference-time.evidence.validate");
            timing?.SetTag("processor.id", cut.ProcessorId.Value);
            timing?.SetTag("revision.position", cut.Position.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            timing?.SetTag("ReferenceTimeEvidenceContract", EvidenceContract);
            var inputs = await historicalReads.ReadAsync(cut, cancellationToken);
            try
            {
                var evidence = await LoadCoreAsync(connection, transaction, cut, inputs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                historicalReads.RequireOwner(connection, transaction);
                timing?.SetTag("stage.completed", true);
                return evidence;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException or OverflowException)
            { throw new ContainmentHistoricalIntegrityException("Malformed historical reference-time authority.", error); }
        }

        private static async Task<ValidatedCutEvidence> LoadCoreAsync(SqlConnection connection, SqlTransaction transaction,
            MetricAggregationCheckpoint cut, IReadOnlyList<PositionedMetricInputFact> inputs, CancellationToken cancellationToken)
        {
            var processor = await SqlServerProductionReferenceTimePublicationTransitionStore.ReadProcessorRowIdAsync(
                connection, transaction, cut.ProcessorId, cancellationToken);
            var quantities = inputs.Where(i => i.Fact.Key is MetricInputFactKeys.PartCountIncrement
                or MetricInputFactKeys.GoodQuantity or MetricInputFactKeys.RejectedQuantity).ToArray();
            if (quantities.Any(i => i.Fact.Unit != MetricInputFactUnits.Count ||
                    (i.Fact.Key == MetricInputFactKeys.PartCountIncrement && i.Fact.SourceQuantityEvidenceId is null)))
                throw new ContainmentHistoricalIntegrityException("Produced quantity lacks canonical units or source identity.");
            ProductionQuantitySourceEntry[] sources;
            try
            {
                sources = quantities.Where(i => i.Fact.SourceQuantityEvidenceId is not null)
                    .GroupBy(i => i.Fact.SourceQuantityEvidenceId!.Value)
                    .Select(g => SqlServerMetricAggregationStore.ReconstructSource(g.Key, g.OrderBy(i => i.Position).ToArray()))
                    .Where(s => s is not null).Select(s => s!).ToArray();
            }
            catch (InvalidOperationException error)
            { throw new ContainmentHistoricalIntegrityException("Conflicting produced-source membership.", error); }

            var transitions = await ReadTransitionChainAsync(connection, transaction, cut, processor, cancellationToken);
            var reference = transitions[^1].CompletedReferenceTimeRevision!.Value.Value;
            using var outcomesTiming = OperationalMetricReportingDiagnostics.Source.StartActivity("historical.reference-time.outcomes.read");
            var outcomes = new List<PublishedProductionReferenceTimeOutcome>();
            await using (var command = Command(connection, transaction,
                SqlServerProductionReferenceTimeOutcomeStore.OutcomeSelect +
                " WHERE o.MetricAggregationProcessorRowId = @Processor AND o.ProductionReferenceTimeRevision <= @R ORDER BY o.ProductionReferenceTimeRevision;", processor))
            {
                command.Parameters.Add(SqlServerUInt64.CreateParameter("@R", checked((ulong)reference)));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) outcomes.Add(SqlServerProductionReferenceTimeOutcomeStore.Materialize(reader));
            }
            outcomesTiming?.SetTag("input.count", outcomes.Count);
            outcomesTiming?.SetTag("stage.completed", true);
            outcomesTiming?.Dispose();
            using var semanticTiming = OperationalMetricReportingDiagnostics.Source.StartActivity("historical.reference-time.semantic.validate");
            await ValidateSemanticsAsync(inputs, sources, transitions, outcomes, reference,
                (standard, token) => ReadStandardCutAsync(connection, transaction, standard, token), cancellationToken);
            semanticTiming?.SetTag("stage.completed", true);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValidatedCutEvidence(connection, transaction, cut, reference, sources, outcomes);
        }

        internal VerifiedReferenceTime EvaluatePeriod(SqlConnection connection, SqlTransaction transaction,
            OperationalMetricPeriodId period, CancellationToken cancellationToken)
        {
            RequireOwner(connection, transaction, Cut);
            cancellationToken.ThrowIfCancellationRequested();
            if (period is not (OperationalMetricPeriodId.Shift or OperationalMetricPeriodId.ProductionDay))
                throw new ContainmentHistoricalIntegrityException("Unsupported historical period.");
            using var timing = OperationalMetricReportingDiagnostics.Source.StartActivity("historical.reference-time.period.evaluate");
            timing?.SetTag("processor.id", Cut.ProcessorId.Value);
            timing?.SetTag("revision.position", Cut.Position.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                var result = EvaluatePeriodCore(period);
                cancellationToken.ThrowIfCancellationRequested();
                RequireOwner(connection, transaction, Cut);
                timing?.SetTag("stage.completed", true);
                return result;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException or OverflowException)
            { throw new ContainmentHistoricalIntegrityException("Malformed historical reference-time authority.", error); }
        }

        private VerifiedReferenceTime EvaluatePeriodCore(OperationalMetricPeriodId period)
        {
            var relevant = _sources.Where(s => Belongs(s, period)).ToArray();
            var relevantIds = relevant.Select(s => s.Evidence.Id).ToHashSet();
            var resolutions = _outcomes.Where(o => relevantIds.Contains(o.SourceQuantityEvidenceId)).ToArray();
            var binding = new ContainmentHistoricalAuthority(_reference,
                relevantIds.Select(id => id.Value).Order(StringComparer.Ordinal), resolutions.Select(o => o.AuthorityRevision).Distinct().Order());
            var duration = ProductionReferenceTimeCompleteness.SumIdealDurationSeconds(relevant.Select(s => s.Evidence.Id).ToArray(),
                resolutions, Cut.StreamId.MachineId, (period as OperationalMetricPeriodId.Shift)?.ShiftOccurrenceId,
                (period as OperationalMetricPeriodId.ProductionDay)?.ProductionDayId);
            return new(binding, duration is null ? null : new MetricAggregateValue(duration.Value, MetricInputFactUnits.Seconds,
                resolutions.LongLength, resolutions.Min(o => o.OccurredAtUtc), resolutions.Max(o => o.OccurredAtUtc)));
        }
    }

    // One set-based read replaces the previous revision-list + 2 SQL calls per revision.
    // LEFT JOINs preserve absent transition/cut detection; every predecessor and R boundary is
    // still checked, at this exact processor/cut, inside the caller's Serializable transaction.
    internal static async Task<IReadOnlyList<ProductionReferenceTimePublicationTransition>> ReadTransitionChainAsync(
        SqlConnection connection, SqlTransaction transaction, MetricAggregationCheckpoint cut, long processor,
        CancellationToken cancellationToken)
    {
        SqlContainmentHistoricalContributionReader.RequireStableTransaction(connection, transaction);
        using var timing = OperationalMetricReportingDiagnostics.Source.StartActivity("historical.reference-time.transition-chain");
        timing?.SetTag("processor.id", cut.ProcessorId.Value);
        timing?.SetTag("revision.position", cut.Position.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var transitions = new List<ProductionReferenceTimePublicationTransition>();
        await using var command = Command(connection, transaction, """
            SELECT r.Position, t.TargetMetricAggregationPosition,
                   t.ExpectedPreviousMetricAggregationPosition, t.ProductionStandardAuthorityRevision,
                   t.StartingProductionReferenceTimeRevision, t.State, t.FinalProductionReferenceTimeRevision,
                   (SELECT COUNT_BIG(*) FROM dbo.ProductionReferenceTimePublicationCut p
                    WHERE p.MetricAggregationProcessorRowId = r.MetricAggregationProcessorRowId
                      AND p.MetricAggregationPosition = r.Position
                      AND p.ProductionReferenceTimeRevision = t.FinalProductionReferenceTimeRevision)
            FROM dbo.MetricAggregationRevision r
            LEFT JOIN dbo.ProductionReferenceTimePublicationTransition t
              ON t.MetricAggregationProcessorRowId = r.MetricAggregationProcessorRowId
             AND t.TargetMetricAggregationPosition = r.Position
            WHERE r.MetricAggregationProcessorRowId = @Processor AND r.Position <= @A
            ORDER BY r.Position;
            """, processor);
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@A", cut.Position.Value));
        MetricInputPosition? previous = null;
        long reference = 0;
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            var revision = new MetricInputPosition(SqlServerUInt64.Materialize(rows.GetDecimal(0)));
            if (rows.IsDBNull(1) || rows.GetByte(5) != 1 || rows.IsDBNull(6))
                throw new MissingContainmentHistoricalAuthorityException("A historical reference-time transition is absent or pending.");
            var transition = new ProductionReferenceTimePublicationTransition(cut.ProcessorId, revision,
                rows.IsDBNull(2) ? null : new MetricInputPosition(SqlServerUInt64.Materialize(rows.GetDecimal(2))),
                rows.IsDBNull(3) ? null : checked((long)rows.GetDecimal(3)),
                new ProductionReferenceTimeAuthorityRevision(checked((long)rows.GetDecimal(4))),
                new ProductionReferenceTimeAuthorityRevision(checked((long)rows.GetDecimal(6))), true);
            if (transition.ExpectedPreviousAggregationPosition != previous || transition.StartingReferenceTimeRevision.Value != reference)
                throw new ContainmentHistoricalIntegrityException("Historical reference-time transitions do not form the exact A/R chain.");
            reference = transition.CompletedReferenceTimeRevision!.Value.Value;
            if (rows.GetInt64(7) != 1)
                throw new MissingContainmentHistoricalAuthorityException("The completed (A,R) publication cut is absent.");
            transitions.Add(transition);
            previous = revision;
        }
        if (previous != cut.Position)
            throw new MissingContainmentHistoricalAuthorityException("The exact aggregation revision is absent.");
        timing?.SetTag("input.count", transitions.Count);
        timing?.SetTag("sql.command.count", 1);
        timing?.SetTag("stage.completed", true);
        return transitions.AsReadOnly();
    }

    private static bool Belongs(ProductionQuantitySourceEntry source, OperationalMetricPeriodId period) => period switch
    {
        OperationalMetricPeriodId.Shift shift => source.ShiftOccurrenceId == shift.ShiftOccurrenceId,
        OperationalMetricPeriodId.ProductionDay day => source.ProductionDayId == day.ProductionDayId,
        _ => throw new ArgumentException("Unsupported historical period.", nameof(period))
    };

    private static async Task<ProductionStandardAuthorityCut> ReadStandardCutAsync(SqlConnection connection,
        SqlTransaction transaction, long revision, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.ProductionStandardAuthorityRevision WHERE ProductionStandardAuthorityRevision <= @S;";
            command.Parameters.AddWithValue("@S", revision);
            if ((decimal)(long)(await command.ExecuteScalarAsync(cancellationToken))! != (decimal)revision + 1m)
                throw new MissingContainmentHistoricalAuthorityException("The historical standard authority revision chain is incomplete.");
        }
        var versions = new List<ProductionStandardVersion>();
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT VersionId, ProductionStandardAuthorityRevision, CompanyId, SiteId, PartId, OperationId,
                   MachineId, SecondsPerUnit, EffectiveFromUtc, EffectiveToUtc, SourceReference
            FROM dbo.ProductionStandardVersion WHERE ProductionStandardAuthorityRevision <= @S
            ORDER BY ProductionStandardAuthorityRevision;
            """;
        read.Parameters.AddWithValue("@S", revision);
        await using var rows = await read.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken)) versions.Add(SqlServerProductionStandardAuthority.Materialize(rows));
        if (versions.Count != revision)
            throw new MissingContainmentHistoricalAuthorityException("The historical standard cut lacks its complete version inventory.");
        for (var i = 0; i < versions.Count; i++)
            if (versions[i].PublishedRevision != (long)i + 1)
                throw new ContainmentHistoricalIntegrityException("The standard version inventory contradicts the publication revision chain.");
        return ProductionStandardAuthorityCut.Materialize(revision, versions);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql, long processor)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.Add("@Processor", SqlDbType.BigInt).Value = processor;
        return command;
    }
}
