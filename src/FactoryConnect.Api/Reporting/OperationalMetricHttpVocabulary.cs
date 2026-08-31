using FactoryConnect.Abstractions;

namespace FactoryConnect.Api.Reporting;

internal static class OperationalMetricHttpVocabulary
{
    public const string Calculated = "calculated";
    public const string Unavailable = "unavailable";
    public const string InsufficientEvidence = "insufficient-evidence";
    public const string PeriodAscending = "period-ascending";
    public const string PeriodDescending = "period-descending";
    public const string ShiftScope = "shift";
    public const string ProductionDayScope = "production-day";

    public static IReadOnlyList<string> Statuses { get; } =
    [
        Calculated,
        Unavailable,
        InsufficientEvidence,
    ];

    public static IReadOnlyList<string> Orders { get; } =
    [
        PeriodAscending,
        PeriodDescending,
    ];

    public static IReadOnlyList<string> Scopes { get; } =
    [
        ShiftScope,
        ProductionDayScope,
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

    public static OperationalMetricReportOrder ParseOrder(string value) => value switch
    {
        PeriodAscending => OperationalMetricReportOrder.PeriodAscending,
        PeriodDescending => OperationalMetricReportOrder.PeriodDescending,
        _ => throw new ArgumentException($"Unsupported operational metric report order '{value}'.", nameof(value)),
    };
}
