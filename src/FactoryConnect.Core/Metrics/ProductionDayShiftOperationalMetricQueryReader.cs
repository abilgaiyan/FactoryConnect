using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class ProductionDayShiftOperationalMetricQueryReader :
    IProductionDayShiftOperationalMetricQueryReader
{
    private readonly IProductionDayShiftOperationalMetricReader _reader;

    public ProductionDayShiftOperationalMetricQueryReader(
        IProductionDayShiftOperationalMetricReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async ValueTask<ReportingPage<ProductionDayShiftOperationalMetricReport>> ReadAsync(
        ProductionDayShiftOperationalMetricPageQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var cursor = query.Page.ContinuationToken is null
            ? null
            : ProductionDayShiftReportingCursor.Decode(
                query.Page.ContinuationToken,
                query.Selection);

        var reports = await _reader.ReadAsync(query.Selection, cancellationToken)
            .ConfigureAwait(false);
        ValidateResults(query.Selection, reports);

        var ordered = reports
            .OrderBy(static report => report.Source.MachineId.Value)
            .ThenBy(static report => report.Source.ProcessorId.Value, StringComparer.Ordinal)
            .ThenBy(static report => report.ProductionDayId.BusinessDate)
            .ThenBy(static report => report.ProductionDayId.SiteId.Value, StringComparer.Ordinal)
            .ThenBy(static report => report.ShiftOccurrenceId.StartsAtUtc)
            .ThenBy(static report => report.ShiftOccurrenceId.EndsAtUtc)
            .ThenBy(static report => report.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value, StringComparer.Ordinal)
            .ThenBy(static report => report.ShiftOccurrenceId.ShiftId.Value, StringComparer.Ordinal)
            .ToArray();

        var candidates = cursor is null
            ? ordered
            : ordered.Where(report => Compare(report, cursor) > 0).ToArray();
        var hasMore = candidates.Length > query.Page.PageSize;
        var items = candidates.Take(query.Page.PageSize).ToArray();
        var continuation = hasMore
            ? ProductionDayShiftReportingCursor.Encode(
                query.Selection,
                ProductionDayShiftReportingCursor.CursorKey.From(items[^1]))
            : null;

        return new ReportingPage<ProductionDayShiftOperationalMetricReport>(
            items,
            continuation);
    }

    private static void ValidateResults(
        ProductionDayShiftOperationalMetricQuery query,
        IReadOnlyList<ProductionDayShiftOperationalMetricReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var identities = new HashSet<ReportIdentity>();
        foreach (var report in reports)
        {
            if (report is null)
            {
                throw new InvalidDataException(
                    "Production-day shift reporting reader returned a null report.");
            }

            if (report.ContextKey != query.ContextKey)
            {
                throw new InvalidDataException(
                    "Production-day shift reporting reader returned a report outside the requested context.");
            }

            var matchingSelection = query.Sources.Any(selection =>
                selection.Source.MachineId == report.Source.MachineId &&
                selection.Source.ProcessorId == report.Source.ProcessorId &&
                selection.ProductionDayId == report.ProductionDayId);
            if (!matchingSelection)
            {
                throw new InvalidDataException(
                    "Production-day shift reporting reader returned a report outside the requested source/production-day selection.");
            }

            if (report.ShiftOccurrenceId.SiteId != report.ProductionDayId.SiteId)
            {
                throw new InvalidDataException(
                    "Production-day shift reporting reader returned a shift outside its authoritative production-day site.");
            }

            foreach (var metric in report.Metrics)
            {
                if (query.Metrics is not null &&
                    !query.Metrics.DefinitionIds.Contains(metric.DefinitionId))
                {
                    throw new InvalidDataException(
                        "Production-day shift reporting reader returned an unrequested metric definition.");
                }

                if (query.Statuses is not null &&
                    !query.Statuses.Statuses.Contains(metric.Status))
                {
                    throw new InvalidDataException(
                        "Production-day shift reporting reader returned an unrequested metric status.");
                }
            }

            var identity = new ReportIdentity(
                report.Source.MachineId,
                report.Source.ProcessorId,
                report.ProductionDayId,
                report.ShiftOccurrenceId,
                report.ContextKey);
            if (!identities.Add(identity))
            {
                throw new InvalidDataException(
                    "Production-day shift reporting reader returned duplicate authoritative shift report identities.");
            }
        }
    }

    private static int Compare(
        ProductionDayShiftOperationalMetricReport report,
        ProductionDayShiftReportingCursor.CursorKey cursor)
    {
        var comparison = report.Source.MachineId.Value.CompareTo(cursor.MachineId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            report.Source.ProcessorId.Value,
            cursor.ProcessorId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = report.ProductionDayId.BusinessDate.CompareTo(cursor.BusinessDate);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(report.ProductionDayId.SiteId.Value, cursor.SiteId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = report.ShiftOccurrenceId.StartsAtUtc.CompareTo(cursor.StartsAtUtc);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = report.ShiftOccurrenceId.EndsAtUtc.CompareTo(cursor.EndsAtUtc);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            report.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value,
            cursor.ShiftScheduleAssignmentId);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(
                report.ShiftOccurrenceId.ShiftId.Value,
                cursor.ShiftId);
    }

    private sealed record ReportIdentity(
        MachineId MachineId,
        OperationalMetricProjectionProcessorId ProcessorId,
        ProductionDayId ProductionDayId,
        ShiftOccurrenceId ShiftOccurrenceId,
        OperationalMetricEvaluationContextKey ContextKey);
}

internal static class ProductionDayShiftReportingCursor
{
    private const int CurrentVersion = 2;

    public static ReportingContinuationToken Encode(
        ProductionDayShiftOperationalMetricQuery query,
        CursorKey key)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(key);

        var payload = new CursorPayload(
            CurrentVersion,
            CreateQueryFingerprint(query),
            key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return new ReportingContinuationToken(ToBase64Url(bytes));
    }

    public static CursorKey Decode(
        ReportingContinuationToken token,
        ProductionDayShiftOperationalMetricQuery query)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(FromBase64Url(token.Value));
            if (payload is null || payload.Version != CurrentVersion || payload.Key is null)
            {
                throw new FormatException("Unsupported or incomplete continuation token payload.");
            }

            if (!StringComparer.Ordinal.Equals(
                    payload.QueryFingerprint,
                    CreateQueryFingerprint(query)))
            {
                throw new IncompatibleReportingContinuationTokenException();
            }

            payload.Key.Validate();
            return payload.Key;
        }
        catch (IncompatibleReportingContinuationTokenException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or ArgumentException or OverflowException)
        {
            throw new MalformedReportingContinuationTokenException(exception);
        }
    }

    private static string CreateQueryFingerprint(ProductionDayShiftOperationalMetricQuery query)
    {
        var value = new StringBuilder();
        Append(value, CurrentVersion.ToString(CultureInfo.InvariantCulture));
        Append(value, "production-day-shifts");

        foreach (var selection in query.Sources
            .OrderBy(static selection => selection.Source.MachineId.Value)
            .ThenBy(static selection => selection.Source.ProcessorId.Value, StringComparer.Ordinal)
            .ThenBy(static selection => selection.ProductionDayId.BusinessDate)
            .ThenBy(static selection => selection.ProductionDayId.SiteId.Value, StringComparer.Ordinal))
        {
            Append(value, selection.Source.MachineId.Value.ToString("D", CultureInfo.InvariantCulture));
            Append(value, selection.Source.ProcessorId.Value);
            Append(value, selection.ProductionDayId.SiteId.Value);
            Append(value, selection.ProductionDayId.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        Append(value, "sources-end");
        var context = query.ContextKey;
        Append(value, context.ProductionOrderId?.Value);
        Append(value, context.OperationId?.Value);
        Append(value, context.PartId?.Value);
        Append(value, context.OperatorId?.Value);

        if (query.Metrics is not null)
        {
            foreach (var definition in query.Metrics.DefinitionIds
                .OrderBy(static definition => definition.MetricKey, StringComparer.Ordinal)
                .ThenBy(static definition => definition.Version, StringComparer.Ordinal))
            {
                Append(value, definition.MetricKey);
                Append(value, definition.Version);
            }
        }

        Append(value, "metrics-end");
        if (query.Statuses is not null)
        {
            foreach (var status in query.Statuses.Statuses.OrderBy(static status => status))
            {
                Append(value, ((int)status).ToString(CultureInfo.InvariantCulture));
            }
        }

        Append(value, "statuses-end");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-:");
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder != 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private sealed record CursorPayload(
        int Version,
        string QueryFingerprint,
        CursorKey Key);

    internal sealed record CursorKey(
        Guid MachineId,
        string ProcessorId,
        string SiteId,
        DateOnly BusinessDate,
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc,
        string ShiftScheduleAssignmentId,
        string ShiftId)
    {
        public static CursorKey From(ProductionDayShiftOperationalMetricReport report) =>
            new(
                report.Source.MachineId.Value,
                report.Source.ProcessorId.Value,
                report.ProductionDayId.SiteId.Value,
                report.ProductionDayId.BusinessDate,
                report.ShiftOccurrenceId.StartsAtUtc,
                report.ShiftOccurrenceId.EndsAtUtc,
                report.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value,
                report.ShiftOccurrenceId.ShiftId.Value);

        public void Validate()
        {
            if (MachineId == Guid.Empty ||
                string.IsNullOrWhiteSpace(ProcessorId) ||
                string.IsNullOrWhiteSpace(SiteId) ||
                BusinessDate == default ||
                string.IsNullOrWhiteSpace(ShiftScheduleAssignmentId) ||
                string.IsNullOrWhiteSpace(ShiftId) ||
                StartsAtUtc.Offset != TimeSpan.Zero ||
                EndsAtUtc.Offset != TimeSpan.Zero ||
                EndsAtUtc <= StartsAtUtc)
            {
                throw new ArgumentException("Continuation token contains an invalid shift cursor identity.");
            }
        }
    }
}
