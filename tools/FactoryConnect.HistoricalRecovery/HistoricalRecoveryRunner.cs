using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.HistoricalRecovery;

public sealed class HistoricalReconstruction(IHistoricalRecoveryReader reader)
{
    public const string Baseline = "348e6167f9a8b7e8784732ca8cc8a14559837d08";
    private readonly OperationalMetricDefinitionCatalog _catalog =
        new(BuiltInOperationalMetricDefinitions.All);

    public async Task<ReconstructionRecord> RunAsync(
        RecoveryTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var operands = CreateOperands(_catalog);
        var first = await reader.ReadAsync(target, operands, cancellationToken).ConfigureAwait(false);
        ValidateCapture(target, first, operands);
        var selected = first.PrefixFacts.Where(fact => fact.ProductionDayId == target.Day).ToArray();
        var aggregates = MetricInputContributionAggregator.Aggregate(target.Revision.StreamId, selected);
        Reconcile(target, first, operands, aggregates);

        var evaluation1 = await EvaluateAsync(first.Snapshot, cancellationToken).ConfigureAwait(false);
        var evaluation2 = await EvaluateAsync(first.Snapshot, cancellationToken).ConfigureAwait(false);
        if (!Equivalent(evaluation1, evaluation2))
        {
            throw new InvalidDataException("Repeated evaluation changed outcomes or recursive evidence.");
        }

        var second = await reader.ReadAsync(target, operands, cancellationToken).ConfigureAwait(false);
        ValidateCapture(target, second, operands);
        var fingerprint = HistoricalFingerprint(first);
        if (!string.Equals(fingerprint, HistoricalFingerprint(second), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Historical inputs changed during diagnostic capture. No recoverability result issued.");
        }

        return new ReconstructionRecord(
            "1.0", Baseline, target, DefinitionAuthority.Conditional,
            RecoveryJson.Definitions(_catalog), fingerprint, first, selected,
            RecoveryJson.Element(aggregates.ProductionDayContributions),
            evaluation1, evaluation2, "PASS", "PASS",
            "RECOVERABLE_WITHIN_IDENTIFIED_DEFINITION_CANDIDATE_SET",
            "All currently persisted prefix facts have exact recorded contribution membership; " +
            "all recorded target-period contributions reconcile with the historical reader. " +
            "This does not prove that upstream evidence was never omitted before persistence. " +
            "Live authority captures are observations, not a simultaneous fleet/processor cut or proof against external writers.",
            second.LiveAuthority);
    }

    public static IReadOnlyList<OperationalMetricOperandDefinition> CreateOperands(
        IOperationalMetricDefinitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var operands = new Dictionary<string, OperationalMetricOperandDefinition>(StringComparer.Ordinal);
        foreach (var definition in catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.ProductionDay))
        {
            foreach (var operand in definition.Operands)
            {
                if (operand.Source is not OperationalMetricOperandSource.Component component)
                {
                    continue;
                }

                var canonical = operand with { OperandName = component.ComponentKey };
                if (operands.TryGetValue(component.ComponentKey, out var existing) &&
                    (existing.RequiredDimension != canonical.RequiredDimension ||
                     !string.Equals(existing.RequiredUnit, canonical.RequiredUnit, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("Definitions disagree on a component domain/unit.");
                }

                operands[component.ComponentKey] = canonical;
            }
        }

        return operands.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value).ToArray();
    }

    public static void ValidateCapture(
        RecoveryTarget target,
        HistoricalCapture capture,
        IReadOnlyList<OperationalMetricOperandDefinition> operands)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(operands);
        if (capture.Revision.Revision != target.Revision ||
            !capture.Revision.ProductionDayIds.Contains(target.Day) ||
            capture.Snapshot.Revision != target.Revision ||
            capture.Snapshot.EvaluationKey.PeriodId != target.Period ||
            capture.Snapshot.EvaluationKey.MachineId != target.Revision.StreamId.MachineId ||
            capture.Snapshot.EvaluationKey.ContextKey != OperationalMetricEvaluationContextKey.Unpartitioned)
        {
            throw new InvalidDataException("Exact revision/period membership or snapshot identity failed.");
        }

        var facts = capture.PrefixFacts;
        var membership = capture.PrefixMembership;
        if (facts.Count != membership.Count ||
            membership.Select(value => value.FactRowId).Distinct().Count() != membership.Count ||
            membership.Select(value => value.Position).Distinct().Count() != membership.Count ||
            membership.Select(value => value.FactId).Distinct(StringComparer.Ordinal).Count() != membership.Count)
        {
            throw new InvalidDataException("Prefix facts and exact contribution membership differ or contain duplicates.");
        }

        var bindings = membership.ToDictionary(value => value.Position);
        foreach (var fact in facts)
        {
            if (fact.Position > target.Revision.Position ||
                fact.StreamId != target.Revision.StreamId ||
                fact.Fact.MachineId != target.Revision.StreamId.MachineId ||
                !bindings.TryGetValue(fact.Position.Value, out var binding) ||
                !string.Equals(binding.FactId, fact.Fact.Id.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Contribution fact/position/stream binding failed.");
            }
        }

        // Normal aggregator also rejects duplicate fact identities/positions and mixed units.
        _ = MetricInputContributionAggregator.Aggregate(target.Revision.StreamId, facts);
        if (capture.Transition.ProcessorId != target.Revision.ProcessorId ||
            capture.Transition.TargetAggregationPosition != target.Revision.Position ||
            !capture.Transition.IsCompleted || capture.Transition.CompletedReferenceTimeRevision is null)
        {
            throw new InvalidDataException("Required historical prerequisite transition is not completed.");
        }

        if (capture.Transition.ProductionStandardAuthorityRevision is long standard &&
            capture.StandardCut?.Revision != standard)
        {
            throw new InvalidDataException("Required historical production-standard cut is unavailable.");
        }

        if (capture.QuantitySources.AggregationCheckpoint != target.Revision ||
            capture.QuantitySources.PeriodId != target.Period)
        {
            throw new InvalidDataException("Produced-source cut does not match the exact target.");
        }

        var required = operands.ToDictionary(
            operand => ((OperationalMetricOperandSource.Component)operand.Source).ComponentKey,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in capture.Snapshot.Components)
        {
            var key = OperationalMetricComponentKeyMapping.ComponentKey(component.SourceIdentity.ComponentKey);
            if (!seen.Add(key) || !required.TryGetValue(key, out var requirement) ||
                component.OperandName != key ||
                component.SourceIdentity.ProcessorId != target.Revision.ProcessorId ||
                component.SourceIdentity.MachineId != target.Revision.StreamId.MachineId ||
                component.SourceIdentity.PeriodId != target.Period ||
                component.Dimension != requirement.RequiredDimension ||
                component.Aggregate.Unit != requirement.RequiredUnit)
            {
                throw new InvalidDataException("Historical component identity/domain/unit is invalid.");
            }
        }
    }

    public static void Reconcile(
        RecoveryTarget target,
        HistoricalCapture capture,
        IReadOnlyList<OperationalMetricOperandDefinition> operands,
        MetricAggregateContributionSet reconstructed)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(operands);
        ArgumentNullException.ThrowIfNull(reconstructed);
        var aggregates = reconstructed.ProductionDayContributions
            .Where(value => value.Key.ProductionDayId == target.Day)
            .ToDictionary(value => value.Key.MetricInputKey, value => value.Value, StringComparer.Ordinal);
        var components = capture.Snapshot.Components.ToDictionary(
            component => OperationalMetricComponentKeyMapping.ComponentKey(component.SourceIdentity.ComponentKey),
            StringComparer.Ordinal);
        foreach (var operand in operands)
        {
            var key = ((OperationalMetricOperandSource.Component)operand.Source).ComponentKey;
            MetricAggregateValue? expected;
            if (key == MetricInputKeys.ProductionReferenceTime)
            {
                expected = ReconstructReferenceTime(target, capture);
            }
            else
            {
                aggregates.TryGetValue(OperationalMetricComponentKeyMapping.AggregateKey(key), out expected);
                if (expected is null)
                {
                    aggregates.TryGetValue(key, out expected);
                }
            }

            components.TryGetValue(key, out var actual);
            if (expected != actual?.Aggregate)
            {
                throw new InvalidDataException($"Historical component '{key}' does not reconcile with durable inputs.");
            }
        }
    }

    private static MetricAggregateValue? ReconstructReferenceTime(
        RecoveryTarget target, HistoricalCapture capture)
    {
        var sources = capture.QuantitySources.Sources;
        var byId = sources.ToDictionary(source => source.Evidence.Id);
        var resolutions = capture.ReferenceTimeCut
            .Where(outcome => byId.ContainsKey(outcome.SourceQuantityEvidenceId))
            .Select(outcome => outcome.Resolution).ToArray();
        foreach (var resolution in resolutions)
        {
            var source = byId[resolution.SourceQuantityEvidenceId];
            var evidence = source.Evidence;
            if (resolution.CompanyId != evidence.CompanyId || resolution.SiteId != evidence.SiteId ||
                resolution.MachineId != evidence.MachineId || resolution.PartId != evidence.PartId ||
                resolution.OperationId != evidence.OperationId ||
                resolution.ShiftOccurrenceId != source.ShiftOccurrenceId ||
                resolution.ProductionDayId != source.ProductionDayId ||
                resolution.OccurredAtUtc != evidence.OccurredAtUtc ||
                resolution.ProducedUnits != evidence.PartCountIncrement)
            {
                throw new InvalidDataException("Reference-time outcome differs from exact produced-source evidence.");
            }
        }

        var seconds = ProductionReferenceTimeCompleteness.SumIdealDurationSeconds(
            sources.Select(source => source.Evidence.Id).ToArray(), resolutions,
            target.Revision.StreamId.MachineId, null, target.Day);
        return seconds is null ? null : new MetricAggregateValue(
            seconds.Value, MetricInputFactUnits.Seconds, resolutions.LongLength,
            resolutions.Min(value => value.OccurredAtUtc), resolutions.Max(value => value.OccurredAtUtc));
    }

    private async Task<IReadOnlyList<EvaluationRecord>> EvaluateAsync(
        OperationalMetricComponentSnapshot snapshot, CancellationToken cancellationToken)
    {
        var evaluator = new OperationalMetricEvaluator(
            _catalog, new FrozenSnapshotReader(snapshot), snapshot.Revision.ProcessorId);
        var results = new List<EvaluationRecord>();
        foreach (var definition in _catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.ProductionDay))
        {
            var key = new OperationalMetricEvaluationKey(
                snapshot.EvaluationKey.MachineId, snapshot.EvaluationKey.PeriodId,
                definition.Id, snapshot.EvaluationKey.ContextKey);
            results.Add(EvaluationRecord.From(await evaluator.EvaluateAsync(key, cancellationToken).ConfigureAwait(false)));
        }

        return results;
    }

    public static bool Equivalent(IReadOnlyList<EvaluationRecord> left, IReadOnlyList<EvaluationRecord> right) =>
        string.Equals(RecoveryJson.Fingerprint(left), RecoveryJson.Fingerprint(right), StringComparison.Ordinal);

    private static string HistoricalFingerprint(HistoricalCapture capture) => RecoveryJson.Fingerprint(new
    {
        Revision = new
        {
            capture.Revision.Revision,
            Shifts = capture.Revision.ShiftOccurrenceIds.OrderBy(value => value.StartsAtUtc)
                .ThenBy(value => value.ShiftScheduleAssignmentId.Value, StringComparer.Ordinal).ToArray(),
            Days = capture.Revision.ProductionDayIds.OrderBy(value => value.SiteId.Value, StringComparer.Ordinal)
                .ThenBy(value => value.BusinessDate).ToArray(),
        },
        capture.PrefixMembership, capture.PrefixFacts, capture.Snapshot,
        capture.Transition, capture.QuantitySources, capture.ReferenceTimeCut, capture.StandardCut,
    });
}

/// <summary>Only RecoverAsync is a mutation seam. Preview and reconstruction never invoke it.</summary>
public sealed class HistoricalRecoveryRunner(IHistoricalRecoveryReader historical, IReportingAuthorityReader reporting,
    IOperationalMetricProjectionRecoveryStore? recovery = null, IRecoveryDeploymentVerifier? deployment = null)
{
    public async Task<RecoveryPreview> PreviewAsync(RecoveryTarget target, CancellationToken cancellationToken = default)
    {
        RecoveryTargets.Validate(target);
        var record = await new HistoricalReconstruction(historical).RunAsync(target, cancellationToken).ConfigureAwait(false);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var factory = new OperationalMetricProjectionFactory(catalog, RecoveryTargets.Processor);
        var projections = record.Evaluation1.Select(e => factory.Create(ToEvaluation(e)))
            .OrderBy(p => p.Key.DefinitionId.MetricKey, StringComparer.Ordinal).ToArray();
        _ = new OperationalMetricProjectionRecoveryRequest(RecoveryTargets.Processor, target.Day, target.Revision, projections);
        var authority = await reporting.ReadReportingAuthorityAsync(target, cancellationToken).ConfigureAwait(false);
        var payload = RecoveryFingerprint.Payload(record, projections);
        return new("1.0", RecoveryFingerprint.Compute(payload), payload, record, projections, authority,
            Classify(target, projections, authority));
    }

    public async Task<RecoveryApplyRecord> ApplyAsync(RecoveryTarget target, JsonElement previewBundle,
        CancellationToken cancellationToken = default)
    {
        if (recovery is null || deployment is null) throw new InvalidOperationException("Apply services are absent.");
        var payload = previewBundle.GetProperty("CanonicalPayload");
        var fingerprint = previewBundle.GetProperty("Fingerprint").GetString() ?? "";
        if (!RecoveryFingerprint.Matches(payload, fingerprint)) throw new InvalidDataException("Invalid preview bundle fingerprint.");
        var current = await PreviewAsync(target, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal) ||
            !RecoveryFingerprint.CanonicalBytes(current.CanonicalPayload).AsSpan().SequenceEqual(RecoveryFingerprint.CanonicalBytes(payload)))
            throw new InvalidDataException("Preview expired: reconstructed evidence differs. Apply refused.");
        if (current.Classification is not (RecoveryClassification.Recoverable or RecoveryClassification.AlreadyEquivalent))
            throw new InvalidOperationException($"Apply refused: {current.Classification}.");
        var deployed = await deployment.VerifyAsync(cancellationToken).ConfigureAwait(false);
        var result = await recovery.RecoverAsync(new OperationalMetricProjectionRecoveryRequest(
            RecoveryTargets.Processor, target.Day, target.Revision, current.Projections), cancellationToken).ConfigureAwait(false);
        var after = await reporting.ReadReportingAuthorityAsync(target, cancellationToken).ConfigureAwait(false);
        var equivalent = ClassifyTarget(target, current.Projections, after.Retained) == RecoveryClassification.AlreadyEquivalent;
        if (result.Outcome != OperationalMetricProjectionRecoveryOutcome.Conflict && !equivalent)
            throw new InvalidDataException("Recovery returned success but post-read target is not exactly equivalent.");
        return new(current, deployed, result, after, equivalent,
            "External before/after authority observations may include live publication. Equality is not an acceptance condition. " +
            "Non-interference authority is the P0-C exclusive-gated recovery transaction with no checkpoint/manifest DML; " +
            "unrelated retained projections and recursive evidence are captured for comparison, not a global frozen cut.");
    }

    public static RecoveryClassification Classify(RecoveryTarget target, IReadOnlyList<OperationalMetricProjection> expected,
        ReportingAuthority authority)
    {
        if (authority.Processor != RecoveryTargets.Processor || authority.Checkpoint is not { } checkpoint ||
            checkpoint.ProcessorId != target.Revision.ProcessorId || checkpoint.StreamId != target.Revision.StreamId ||
            checkpoint.Position < target.Revision.Position) return RecoveryClassification.InvalidRevisionOrBinding;
        if (authority.Manifest.Any(key => key.PeriodId == target.Period)) return RecoveryClassification.BlockedByLatestManifest;
        return ClassifyTarget(target, expected, authority.Retained);
    }
    private static RecoveryClassification ClassifyTarget(RecoveryTarget target, IReadOnlyList<OperationalMetricProjection> expected,
        IReadOnlyList<OperationalMetricProjection> retained)
    {
        var existing = retained.Where(p => p.Key.PeriodId == target.Period).ToArray();
        if (existing.Select(p => p.Key).Distinct().Count() != existing.Length || existing.Any(p =>
            !expected.Any(e => e.Key == p.Key && OperationalMetricProjectionEquivalence.AreEquivalent(e, p))))
            return RecoveryClassification.Conflict;
        return existing.Length == expected.Count ? RecoveryClassification.AlreadyEquivalent : RecoveryClassification.Recoverable;
    }
    private static OperationalMetricEvaluation ToEvaluation(EvaluationRecord value) => new(value.Key, value.Status,
        value.Value, value.Unit, value.ReasonCode, value.ReasonOperandName, value.SourceRevision, value.OperandEvidence,
        value.DependencyEvidence.Select(d => new OperationalMetricDependencyEvidence(d.OperandName, d.DefinitionId, ToEvaluation(d.Evaluation))));
}
