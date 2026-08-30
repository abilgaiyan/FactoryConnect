using FactoryConnect.Abstractions;

namespace FactoryConnect.Api.Reporting;

internal static class OperationalMetricHttpVocabulary
{
    public const string Calculated = "calculated";
    public const string Unavailable = "unavailable";
    public const string InsufficientEvidence = "insufficient-evidence";

    public static IReadOnlyList<string> Statuses { get; } =
    [
        Calculated,
        Unavailable,
        InsufficientEvidence,
    ];

    public static OperationalMetricEvaluationStatus ParseStatus(string value) => value switch
    {
        Calculated => OperationalMetricEvaluationStatus.Calculated,
        Unavailable => OperationalMetricEvaluationStatus.Unavailable,
        InsufficientEvidence => OperationalMetricEvaluationStatus.InsufficientEvidence,
        _ => throw new ArgumentException($"Unsupported operational metric status '{value}'.", nameof(value)),
    };

    public static string FormatStatus(OperationalMetricEvaluationStatus status) => status switch
    {
        OperationalMetricEvaluationStatus.Calculated => Calculated,
        OperationalMetricEvaluationStatus.Unavailable => Unavailable,
        OperationalMetricEvaluationStatus.InsufficientEvidence => InsufficientEvidence,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
