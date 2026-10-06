using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.HistoricalRecovery;

public sealed record RecoveryTarget(MetricAggregationCheckpoint Revision, ProductionDayId Day)
{
    public OperationalMetricPeriodId Period => new OperationalMetricPeriodId.ProductionDay(Day);
}

public sealed record ContributionIdentity(long FactRowId, ulong Position, string FactId);

public sealed record DefinitionAuthority(
    string Applicability,
    string ExactHistoricalExecutionSha,
    IReadOnlyList<string> CandidateSourceCommits,
    string Qualification)
{
    public static DefinitionAuthority Conditional { get; } = new(
        "EstablishedWithinIdentifiedCandidateSetBySourceAndRegistrationEquivalence",
        "UNESTABLISHED",
        [
            "b46f51986f5e5f56f721c34510f5df50045927ef",
            "116591dbc3fb149d76319d8bb21b07deb7d94514",
            "02f5092646d9072f7c24137d410abc0442ab0c84",
            "7e921e5d50480e7ed7cb1cb41b08bca67f0dcfdd",
        ],
        "Conditional on completeness of the identified credible deployment candidates. " +
        "Not proof of exact execution identity. A newly identified candidate requires re-verification.");
}

public sealed record HistoricalCapture(
    MetricAggregationRevisionChange Revision,
    IReadOnlyList<ContributionIdentity> PrefixMembership,
    IReadOnlyList<PositionedMetricInputFact> PrefixFacts,
    OperationalMetricComponentSnapshot Snapshot,
    ProductionReferenceTimePublicationTransition Transition,
    ProductionQuantitySourceCut QuantitySources,
    IReadOnlyList<PublishedProductionReferenceTimeOutcome> ReferenceTimeCut,
    ProductionStandardAuthorityCut? StandardCut,
    JsonElement LiveAuthority);

public interface IHistoricalRecoveryReader
{
    Task<HistoricalCapture> ReadAsync(
        RecoveryTarget target,
        IReadOnlyList<OperationalMetricOperandDefinition> operands,
        CancellationToken cancellationToken);
}

public sealed record EvaluationRecord(
    OperationalMetricEvaluationKey Key,
    OperationalMetricEvaluationStatus Status,
    decimal? Value,
    string Unit,
    OperationalMetricEvaluationReasonCode? ReasonCode,
    string? ReasonOperandName,
    MetricAggregationCheckpoint SourceRevision,
    IReadOnlyList<MetricOperandEvidence> OperandEvidence,
    IReadOnlyList<DependencyRecord> DependencyEvidence)
{
    public static EvaluationRecord From(OperationalMetricEvaluation evaluation) => new(
        evaluation.Key, evaluation.Status, evaluation.Value, evaluation.Unit,
        evaluation.ReasonCode, evaluation.ReasonOperandName, evaluation.SourceRevision,
        evaluation.OperandEvidence,
        evaluation.DependencyEvidence.Select(static value => new DependencyRecord(
            value.OperandName, value.DefinitionId, From(value.Evaluation))).ToArray());
}

public sealed record DependencyRecord(
    string OperandName, OperationalMetricDefinitionId DefinitionId, EvaluationRecord Evaluation);

public sealed record ReconstructionRecord(
    string SchemaVersion,
    string DiagnosticBaseline,
    RecoveryTarget Target,
    DefinitionAuthority DefinitionAuthority,
    JsonElement Definitions,
    string HistoricalInputFingerprint,
    HistoricalCapture Capture,
    IReadOnlyList<PositionedMetricInputFact> SelectedPeriodFacts,
    JsonElement ReconstructedPeriodAggregates,
    IReadOnlyList<EvaluationRecord> Evaluation1,
    IReadOnlyList<EvaluationRecord> Evaluation2,
    string ExactOutcomeAndRecursiveEvidenceEquivalence,
    string HistoricalReadRepeatEquivalence,
    string Recoverability,
    string CompletenessScope,
    JsonElement LiveAuthorityAfter);

public static class RecoveryJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string Fingerprint<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options)));

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(OperationalMetricPeriodId))
            {
                typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "PeriodType",
                    DerivedTypes =
                    {
                        new JsonDerivedType(typeof(OperationalMetricPeriodId.ProductionDay), "ProductionDay"),
                        new JsonDerivedType(typeof(OperationalMetricPeriodId.Shift), "Shift"),
                    },
                };
            }
        });
        var options = new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = resolver };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static JsonElement Definitions(IOperationalMetricDefinitionCatalog catalog) => Element(
        catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.ProductionDay).Select(definition => new
        {
            definition.Id,
            definition.DisplayName,
            definition.SupportedScopes,
            Operands = definition.Operands.Select(operand => new
            {
                operand.OperandName,
                Component = (operand.Source as OperationalMetricOperandSource.Component)?.ComponentKey,
                Dependency = (operand.Source as OperationalMetricOperandSource.EvaluatedMetric)?.DefinitionId,
                operand.RequiredDimension,
                operand.RequiredUnit,
            }),
            Formula = definition.Formula switch
            {
                OperationalMetricFormula.Ratio ratio => Element(new
                {
                    Kind = "Ratio", ratio.NumeratorOperand, ratio.DenominatorOperand,
                }),
                OperationalMetricFormula.Product product => Element(new
                {
                    Kind = "Product", product.FactorOperands,
                }),
                _ => throw new InvalidDataException("Unknown diagnostic definition formula."),
            },
            definition.ResultUnit,
            definition.DomainConstraints,
            definition.PrecisionPolicy,
        }).ToArray());
}

public static class RecoveryTargets
{
    public const string Machine = "de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed";
    public static OperationalMetricProjectionProcessorId Processor { get; } = new($"operational-metrics:{Machine}:builtins-v1");
    public static RecoveryTarget ForDate(string date)
    {
        var revision = date switch { "2026-10-04" => 1211UL, "2026-10-05" => 1911UL,
            _ => throw new ArgumentException("Only the two approved historical targets are supported.", nameof(date)) };
        return new(new MetricAggregationCheckpoint(new MetricAggregationProcessorId($"metric-aggregation:{Machine}"),
            new MetricInputStreamId(new MachineId(Guid.Parse(Machine)), "metric-inputs"), new MetricInputPosition(revision)),
            new ProductionDayId(new SiteId("CAMPUS-1"), DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
    }
    public static void Validate(RecoveryTarget target)
    {
        if (target != ForDate(target.Day.BusinessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))
            throw new ArgumentException("Target does not match the fixed factory recovery contract.", nameof(target));
    }
}

public sealed record ReportingAuthority(OperationalMetricProjectionProcessorId Processor,
    MetricAggregationCheckpoint? Checkpoint, IReadOnlyList<OperationalMetricEvaluationKey> Manifest,
    IReadOnlyList<OperationalMetricProjection> Retained);

public interface IReportingAuthorityReader
{
    Task<ReportingAuthority> ReadReportingAuthorityAsync(RecoveryTarget target, CancellationToken cancellationToken);
}

public interface IRecoveryDeploymentVerifier
{
    Task<JsonElement> VerifyAsync(CancellationToken cancellationToken);
}

public enum RecoveryClassification { Recoverable, AlreadyEquivalent, Conflict, BlockedByLatestManifest, InvalidRevisionOrBinding }
public sealed record RecoveryPreview(string SchemaVersion, string Fingerprint, JsonElement CanonicalPayload,
    ReconstructionRecord Reconstruction, IReadOnlyList<OperationalMetricProjection> Projections,
    ReportingAuthority Authority, RecoveryClassification Classification);
public sealed record RecoveryApplyRecord(RecoveryPreview RevalidatedPreview, JsonElement DeploymentEvidence,
    OperationalMetricProjectionRecoveryResult Result, ReportingAuthority AuthorityAfter,
    bool TargetExactlyEquivalentAfter, string ObservationQualification);
