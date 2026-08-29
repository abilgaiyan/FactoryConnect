using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricReportingQueryReader :
    IOperationalMetricReportingQueryReader
{
    private readonly IOperationalMetricReportingQueryProvider _provider;

    public OperationalMetricReportingQueryReader(
        IOperationalMetricReportingQueryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public async ValueTask<ReportingPage<OperationalMetricProjectionSummary>> ReadAsync(
        OperationalMetricReportQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var startAfter = query.Page.ContinuationToken is null
            ? null
            : OperationalMetricReportingCursor.Decode(
                query.Page.ContinuationToken,
                query);
        var maximumCount = checked(query.Page.PageSize + 1);
        var window = await _provider.ReadWindowAsync(
            query,
            startAfter,
            maximumCount,
            cancellationToken).ConfigureAwait(false);

        ValidateWindow(query, startAfter, maximumCount, window);

        var hasMore = window.Count > query.Page.PageSize;
        var items = window.Take(query.Page.PageSize).ToArray();
        var continuationToken = hasMore
            ? OperationalMetricReportingCursor.Encode(query, items[^1].Key)
            : null;

        return new ReportingPage<OperationalMetricProjectionSummary>(
            items,
            continuationToken);
    }

    private static void ValidateWindow(
        OperationalMetricReportQuery query,
        OperationalMetricEvaluationKey? startAfter,
        int maximumCount,
        IReadOnlyList<OperationalMetricProjectionSummary> window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Count > maximumCount)
        {
            throw new InvalidDataException(
                "Reporting query provider returned more items than the requested window maximum.");
        }

        var comparer = OperationalMetricReportOrdering.GetEvaluationKeyComparer(query.Order);
        OperationalMetricEvaluationKey? previous = startAfter;

        foreach (var summary in window)
        {
            if (summary is null)
            {
                throw new InvalidDataException(
                    "Reporting query provider returned a null projection summary.");
            }

            if (!OperationalMetricReportQuerySemantics.Matches(query, summary))
            {
                throw new InvalidDataException(
                    "Reporting query provider returned a projection outside the requested query.");
            }

            if (previous is not null && comparer.Compare(previous, summary.Key) >= 0)
            {
                throw new InvalidDataException(
                    "Reporting query provider returned duplicate or non-canonically ordered evaluation identities.");
            }

            previous = summary.Key;
        }
    }
}

internal static class OperationalMetricReportingCursor
{
    private const int CurrentVersion = 1;

    public static ReportingContinuationToken Encode(
        OperationalMetricReportQuery query,
        OperationalMetricEvaluationKey key)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(key);

        var payload = new CursorPayload(
            CurrentVersion,
            CreateQueryFingerprint(query),
            CursorEvaluationKey.From(key));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return new ReportingContinuationToken(ToBase64Url(bytes));
    }

    public static OperationalMetricEvaluationKey Decode(
        ReportingContinuationToken token,
        OperationalMetricReportQuery query)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(
                FromBase64Url(token.Value));
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

            return payload.Key.ToEvaluationKey();
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

    private static string CreateQueryFingerprint(OperationalMetricReportQuery query)
    {
        var value = new StringBuilder();
        Append(value, CurrentVersion.ToString(CultureInfo.InvariantCulture));
        Append(value, query switch
        {
            ShiftOperationalMetricReportQuery => "shift",
            ProductionDayOperationalMetricReportQuery => "production-day",
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        });
        Append(value, ((int)query.Order).ToString(CultureInfo.InvariantCulture));

        foreach (var source in query.Sources.Sources
            .OrderBy(static source => source.MachineId.Value)
            .ThenBy(static source => source.ProcessorId.Value, StringComparer.Ordinal))
        {
            Append(value, source.MachineId.Value.ToString("D", CultureInfo.InvariantCulture));
            Append(value, source.ProcessorId.Value);
        }

        Append(value, "sources-end");
        switch (query)
        {
            case ShiftOperationalMetricReportQuery shift:
                Append(value, shift.StartsAtOrAfterUtc.ToString("O", CultureInfo.InvariantCulture));
                Append(value, shift.StartsBeforeUtc.ToString("O", CultureInfo.InvariantCulture));
                break;
            case ProductionDayOperationalMetricReportQuery productionDay:
                Append(value, productionDay.FromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Append(value, productionDay.ToExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query));
        }

        if (query.Metrics is not null)
        {
            foreach (var definitionId in query.Metrics.DefinitionIds
                .OrderBy(static id => id.MetricKey, StringComparer.Ordinal)
                .ThenBy(static id => id.Version, StringComparer.Ordinal))
            {
                Append(value, definitionId.MetricKey);
                Append(value, definitionId.Version);
            }
        }

        Append(value, "metrics-end");
        Append(value, query.Context?.ProductionOrderId?.Value);
        Append(value, query.Context?.OperationId?.Value);
        Append(value, query.Context?.PartId?.Value);
        Append(value, query.Context?.OperatorId?.Value);

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
    }

    private static string ToBase64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new FormatException("Invalid base64url continuation token."),
        };
        return Convert.FromBase64String(normalized);
    }

    private sealed record CursorPayload(
        int Version,
        string QueryFingerprint,
        CursorEvaluationKey Key);

    private sealed record CursorEvaluationKey(
        string Scope,
        string MachineId,
        string SiteId,
        string? BusinessDate,
        string? ShiftScheduleAssignmentId,
        string? ShiftId,
        string? StartsAtUtc,
        string? EndsAtUtc,
        string? ProductionOrderId,
        string? OperationId,
        string? PartId,
        string? OperatorId,
        string MetricKey,
        string DefinitionVersion)
    {
        public static CursorEvaluationKey From(OperationalMetricEvaluationKey key)
        {
            var period = key.PeriodId switch
            {
                OperationalMetricPeriodId.Shift shift => new PeriodValues(
                    "shift",
                    shift.ShiftOccurrenceId.SiteId.Value,
                    null,
                    shift.ShiftOccurrenceId.ShiftScheduleAssignmentId.Value,
                    shift.ShiftOccurrenceId.ShiftId.Value,
                    shift.ShiftOccurrenceId.StartsAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    shift.ShiftOccurrenceId.EndsAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                OperationalMetricPeriodId.ProductionDay productionDay => new PeriodValues(
                    "production-day",
                    productionDay.ProductionDayId.SiteId.Value,
                    productionDay.ProductionDayId.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    null,
                    null,
                    null,
                    null),
                _ => throw new ArgumentOutOfRangeException(nameof(key)),
            };

            return new CursorEvaluationKey(
                period.Scope,
                key.MachineId.Value.ToString("D", CultureInfo.InvariantCulture),
                period.SiteId,
                period.BusinessDate,
                period.ShiftScheduleAssignmentId,
                period.ShiftId,
                period.StartsAtUtc,
                period.EndsAtUtc,
                key.ContextKey.ProductionOrderId?.Value,
                key.ContextKey.OperationId?.Value,
                key.ContextKey.PartId?.Value,
                key.ContextKey.OperatorId?.Value,
                key.DefinitionId.MetricKey,
                key.DefinitionId.Version);
        }

        public OperationalMetricEvaluationKey ToEvaluationKey()
        {
            var machineId = new MachineId(Guid.Parse(MachineId));
            var siteId = new SiteId(SiteId);
            OperationalMetricPeriodId periodId = Scope switch
            {
                "shift" => new OperationalMetricPeriodId.Shift(
                    new ShiftOccurrenceId(
                        siteId,
                        new ShiftScheduleAssignmentId(Required(ShiftScheduleAssignmentId)),
                        new ShiftId(Required(ShiftId)),
                        DateTimeOffset.ParseExact(
                            Required(StartsAtUtc),
                            "O",
                            CultureInfo.InvariantCulture),
                        DateTimeOffset.ParseExact(
                            Required(EndsAtUtc),
                            "O",
                            CultureInfo.InvariantCulture))),
                "production-day" => new OperationalMetricPeriodId.ProductionDay(
                    new ProductionDayId(
                        siteId,
                        DateOnly.ParseExact(
                            Required(BusinessDate),
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture))),
                _ => throw new FormatException("Unknown reporting cursor period scope."),
            };
            var contextKey = new OperationalMetricEvaluationContextKey
            {
                ProductionOrderId = ProductionOrderId is null
                    ? null
                    : new ProductionOrderId(ProductionOrderId),
                OperationId = OperationId is null ? null : new OperationId(OperationId),
                PartId = PartId is null ? null : new PartId(PartId),
                OperatorId = OperatorId is null ? null : new OperatorId(OperatorId),
            };

            return new OperationalMetricEvaluationKey(
                machineId,
                periodId,
                new OperationalMetricDefinitionId(MetricKey, DefinitionVersion),
                contextKey);
        }

        private static string Required(string? value) => value ??
            throw new FormatException("Reporting cursor is missing required period identity data.");
    }

    private sealed record PeriodValues(
        string Scope,
        string SiteId,
        string? BusinessDate,
        string? ShiftScheduleAssignmentId,
        string? ShiftId,
        string? StartsAtUtc,
        string? EndsAtUtc);
}
