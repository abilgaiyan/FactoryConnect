using FactoryConnect.Abstractions;

namespace FactoryConnect.Api.Reporting;

public sealed record ReportingSourceRequest(Guid MachineId, string ProcessorId);

public sealed record OperationalMetricDefinitionRequest(string MetricKey, string Version);

public sealed record OperationalMetricContextRequest(
    string? ProductionOrderId,
    string? OperationId,
    string? PartId,
    string? OperatorId,
    bool UnpartitionedOnly = false);

public sealed record ShiftOperationalMetricQueryRequest(
    IReadOnlyList<ReportingSourceRequest> Sources,
    DateTimeOffset StartsAtOrAfterUtc,
    DateTimeOffset StartsBeforeUtc,
    IReadOnlyList<OperationalMetricDefinitionRequest>? Metrics,
    OperationalMetricContextRequest? Context,
    IReadOnlyList<string>? Statuses,
    string Order,
    int PageSize,
    string? ContinuationToken);

public sealed record ProductionDayOperationalMetricQueryRequest(
    IReadOnlyList<ReportingSourceRequest> Sources,
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    IReadOnlyList<OperationalMetricDefinitionRequest>? Metrics,
    OperationalMetricContextRequest? Context,
    IReadOnlyList<string>? Statuses,
    string Order,
    int PageSize,
    string? ContinuationToken);

public sealed record ProductionDayShiftReportingSourceRequest(
    Guid MachineId,
    string ProcessorId,
    string SiteId,
    DateOnly BusinessDate);

public sealed record ProductionDayShiftOperationalMetricQueryRequest(
    IReadOnlyList<ProductionDayShiftReportingSourceRequest> Sources,
    OperationalMetricContextRequest? Context,
    IReadOnlyList<OperationalMetricDefinitionRequest>? Metrics,
    IReadOnlyList<string>? Statuses,
    int PageSize,
    string? ContinuationToken);

public sealed record OperationalMetricContextResponse(
    string? ProductionOrderId,
    string? OperationId,
    string? PartId,
    string? OperatorId);

public sealed record ShiftPeriodResponse(
    string SiteId,
    string ShiftScheduleAssignmentId,
    string ShiftId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc);

public sealed record ProductionDayPeriodResponse(
    string SiteId,
    DateOnly BusinessDate);

public sealed record MetricSourceRevisionResponse(
    string ProcessorId,
    Guid MachineId,
    string StreamKey,
    ulong Position);

public sealed record OperationalMetricItemResponse(
    string Scope,
    string ProcessorId,
    Guid MachineId,
    ShiftPeriodResponse? Shift,
    ProductionDayPeriodResponse? ProductionDay,
    OperationalMetricContextResponse Context,
    string MetricKey,
    string DefinitionVersion,
    string Status,
    decimal? Value,
    string Unit,
    string? ReasonCode,
    string? ReasonOperandName,
    MetricSourceRevisionResponse SourceRevision);

public sealed record OperationalMetricPageResponse(
    IReadOnlyList<OperationalMetricItemResponse> Items,
    string? ContinuationToken);

public sealed record ProductionDayShiftMetricResponse(
    string MetricKey,
    string DefinitionVersion,
    string Status,
    decimal? Value,
    string Unit,
    string? ReasonCode,
    string? ReasonOperandName);

public sealed record ProductionDayShiftOperationalMetricResponse(
    string ProcessorId,
    Guid MachineId,
    ProductionDayPeriodResponse ProductionDay,
    string ProductionLineId,
    ShiftPeriodResponse Shift,
    OperationalMetricContextResponse Context,
    MetricSourceRevisionResponse? SourceRevision,
    IReadOnlyList<ProductionDayShiftMetricResponse> Metrics);

public sealed record ProductionDayShiftOperationalMetricPageResponse(
    IReadOnlyList<ProductionDayShiftOperationalMetricResponse> Items,
    string? ContinuationToken);

internal static class OperationalMetricHttpMapper
{
    public static ShiftOperationalMetricReportQuery ToQuery(ShiftOperationalMetricQueryRequest request) =>
        new(
            ToSources(request.Sources),
            request.StartsAtOrAfterUtc,
            request.StartsBeforeUtc,
            ToMetrics(request.Metrics),
            ToContext(request.Context),
            ToStatuses(request.Statuses),
            OperationalMetricHttpVocabulary.ParseOrder(request.Order),
            ToPage(request.PageSize, request.ContinuationToken));

    public static ProductionDayOperationalMetricReportQuery ToQuery(
        ProductionDayOperationalMetricQueryRequest request) =>
        new(
            ToSources(request.Sources),
            request.FromInclusive,
            request.ToExclusive,
            ToMetrics(request.Metrics),
            ToContext(request.Context),
            ToStatuses(request.Statuses),
            OperationalMetricHttpVocabulary.ParseOrder(request.Order),
            ToPage(request.PageSize, request.ContinuationToken));

    public static ProductionDayShiftOperationalMetricPageQuery ToQuery(
        ProductionDayShiftOperationalMetricQueryRequest request) =>
        new(
            new ProductionDayShiftOperationalMetricQuery(
                request.Sources.Select(static source => new ProductionDayShiftReportingSource(
                    new OperationalMetricReportingSource(
                        new MachineId(source.MachineId),
                        new OperationalMetricProjectionProcessorId(source.ProcessorId)),
                    new ProductionDayId(
                        new SiteId(source.SiteId),
                        source.BusinessDate))),
                ToExactContext(request.Context),
                ToMetrics(request.Metrics),
                ToStatuses(request.Statuses)),
            ToPage(request.PageSize, request.ContinuationToken));

    public static OperationalMetricPageResponse ToResponse(
        ReportingPage<OperationalMetricQueryItem> page) =>
        new(
            page.Items.Select(ToResponse).ToArray(),
            page.ContinuationToken?.Value);

    public static ProductionDayShiftOperationalMetricPageResponse ToResponse(
        ReportingPage<ProductionDayShiftOperationalMetricReport> page) =>
        new(
            page.Items.Select(ToResponse).ToArray(),
            page.ContinuationToken?.Value);

    private static OperationalMetricItemResponse ToResponse(OperationalMetricQueryItem item)
    {
        var shift = item is ShiftOperationalMetricQueryItem shiftItem
            ? new ShiftPeriodResponse(
                shiftItem.ShiftOccurrenceId.SiteId.Value,
                shiftItem.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value,
                shiftItem.ShiftOccurrenceId.ShiftId.Value,
                shiftItem.ShiftOccurrenceId.StartsAtUtc,
                shiftItem.ShiftOccurrenceId.EndsAtUtc)
            : null;
        var productionDay = item is ProductionDayOperationalMetricQueryItem dayItem
            ? new ProductionDayPeriodResponse(
                dayItem.ProductionDayId.SiteId.Value,
                dayItem.ProductionDayId.BusinessDate)
            : null;
        var context = item.ContextKey;
        var revision = item.SourceRevision;

        return new OperationalMetricItemResponse(
            item is ShiftOperationalMetricQueryItem
                ? OperationalMetricHttpVocabulary.ShiftScope
                : OperationalMetricHttpVocabulary.ProductionDayScope,
            item.ProcessorId.Value,
            item.MachineId.Value,
            shift,
            productionDay,
            ToResponse(context),
            item.DefinitionId.MetricKey,
            item.DefinitionId.Version,
            OperationalMetricHttpVocabulary.FormatStatus(item.Status),
            item.Value,
            item.Unit,
            item.ReasonCode?.ToString(),
            item.ReasonOperandName,
            ToResponse(revision));
    }

    private static ProductionDayShiftOperationalMetricResponse ToResponse(
        ProductionDayShiftOperationalMetricReport report) =>
        new(
            report.Source.ProcessorId.Value,
            report.Source.MachineId.Value,
            new ProductionDayPeriodResponse(
                report.ProductionDayId.SiteId.Value,
                report.ProductionDayId.BusinessDate),
            report.ProductionLineId.Value,
            new ShiftPeriodResponse(
                report.ShiftOccurrenceId.SiteId.Value,
                report.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value,
                report.ShiftOccurrenceId.ShiftId.Value,
                report.ShiftOccurrenceId.StartsAtUtc,
                report.ShiftOccurrenceId.EndsAtUtc),
            ToResponse(report.ContextKey),
            report.SourceRevision is null ? null : ToResponse(report.SourceRevision),
            report.Metrics.Select(static metric => new ProductionDayShiftMetricResponse(
                metric.DefinitionId.MetricKey,
                metric.DefinitionId.Version,
                OperationalMetricHttpVocabulary.FormatStatus(metric.Status),
                metric.Value,
                metric.Unit,
                metric.ReasonCode?.ToString(),
                metric.ReasonOperandName)).ToArray());

    private static OperationalMetricContextResponse ToResponse(
        OperationalMetricEvaluationContextKey context) =>
        new(
            context.ProductionOrderId?.Value,
            context.OperationId?.Value,
            context.PartId?.Value,
            context.OperatorId?.Value);

    private static MetricSourceRevisionResponse ToResponse(
        MetricAggregationCheckpoint revision) =>
        new(
            revision.ProcessorId.Value,
            revision.StreamId.MachineId.Value,
            revision.StreamId.StreamKey,
            revision.Position.Value);

    private static OperationalMetricReportingSourceSelection ToSources(
        IReadOnlyList<ReportingSourceRequest> sources) =>
        new(sources.Select(static source => new OperationalMetricReportingSource(
            new MachineId(source.MachineId),
            new OperationalMetricProjectionProcessorId(source.ProcessorId))));

    private static OperationalMetricDefinitionSelection? ToMetrics(
        IReadOnlyList<OperationalMetricDefinitionRequest>? metrics) =>
        metrics is null
            ? null
            : new OperationalMetricDefinitionSelection(metrics.Select(static metric =>
                new OperationalMetricDefinitionId(metric.MetricKey, metric.Version)));

    private static OperationalMetricContextFilter? ToContext(OperationalMetricContextRequest? context) =>
        context is null
            ? null
            : new OperationalMetricContextFilter
            {
                UnpartitionedOnly = context.UnpartitionedOnly,
                ProductionOrderId = context.ProductionOrderId is null ? null : new ProductionOrderId(context.ProductionOrderId),
                OperationId = context.OperationId is null ? null : new OperationId(context.OperationId),
                PartId = context.PartId is null ? null : new PartId(context.PartId),
                OperatorId = context.OperatorId is null ? null : new OperatorId(context.OperatorId),
            };

    private static OperationalMetricEvaluationContextKey ToExactContext(
        OperationalMetricContextRequest? context)
    {
        if (context is null || context.UnpartitionedOnly)
        {
            return OperationalMetricEvaluationContextKey.Unpartitioned;
        }

        return new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = context.ProductionOrderId is null ? null : new ProductionOrderId(context.ProductionOrderId),
            OperationId = context.OperationId is null ? null : new OperationId(context.OperationId),
            PartId = context.PartId is null ? null : new PartId(context.PartId),
            OperatorId = context.OperatorId is null ? null : new OperatorId(context.OperatorId),
        };
    }

    private static OperationalMetricStatusSelection? ToStatuses(IReadOnlyList<string>? statuses) =>
        statuses is null
            ? null
            : new OperationalMetricStatusSelection(statuses.Select(OperationalMetricHttpVocabulary.ParseStatus));

    private static ReportingPageRequest ToPage(int pageSize, string? continuationToken) =>
        new(
            pageSize,
            continuationToken is null ? null : new ReportingContinuationToken(continuationToken));
}
