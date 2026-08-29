using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public enum OperationalMetricReportOrder
{
    PeriodAscending,
    PeriodDescending,
}

public static class OperationalMetricReportOrdering
{
    private static readonly IComparer<OperationalMetricEvaluationKey> PeriodAscendingComparer =
        new EvaluationKeyComparer(descendingPeriod: false);

    private static readonly IComparer<OperationalMetricEvaluationKey> PeriodDescendingComparer =
        new EvaluationKeyComparer(descendingPeriod: true);

    public static IComparer<OperationalMetricEvaluationKey> GetEvaluationKeyComparer(
        OperationalMetricReportOrder order) => order switch
    {
        OperationalMetricReportOrder.PeriodAscending => PeriodAscendingComparer,
        OperationalMetricReportOrder.PeriodDescending => PeriodDescendingComparer,
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    private sealed class EvaluationKeyComparer(bool descendingPeriod) :
        IComparer<OperationalMetricEvaluationKey>
    {
        public int Compare(
            OperationalMetricEvaluationKey? x,
            OperationalMetricEvaluationKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);

            var periodComparison = ComparePeriod(x.PeriodId, y.PeriodId);
            if (periodComparison != 0)
            {
                return descendingPeriod ? -periodComparison : periodComparison;
            }

            var comparison = x.MachineId.Value.CompareTo(y.MachineId.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareCompletePeriodIdentity(x.PeriodId, y.PeriodId);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareContext(x.ContextKey, y.ContextKey);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                x.DefinitionId.MetricKey,
                y.DefinitionId.MetricKey);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(
                    x.DefinitionId.Version,
                    y.DefinitionId.Version);
        }

        private static int ComparePeriod(
            OperationalMetricPeriodId x,
            OperationalMetricPeriodId y) => (x, y) switch
        {
            (OperationalMetricPeriodId.Shift left, OperationalMetricPeriodId.Shift right) =>
                left.ShiftOccurrenceId.StartsAtUtc.CompareTo(right.ShiftOccurrenceId.StartsAtUtc),
            (OperationalMetricPeriodId.ProductionDay left, OperationalMetricPeriodId.ProductionDay right) =>
                left.ProductionDayId.BusinessDate.CompareTo(right.ProductionDayId.BusinessDate),
            _ => throw new ArgumentException(
                "Reporting evaluation keys from different period scopes cannot be ordered together."),
        };

        private static int CompareCompletePeriodIdentity(
            OperationalMetricPeriodId x,
            OperationalMetricPeriodId y) => (x, y) switch
        {
            (OperationalMetricPeriodId.Shift left, OperationalMetricPeriodId.Shift right) =>
                CompareShiftIdentity(left.ShiftOccurrenceId, right.ShiftOccurrenceId),
            (OperationalMetricPeriodId.ProductionDay left, OperationalMetricPeriodId.ProductionDay right) =>
                StringComparer.Ordinal.Compare(
                    left.ProductionDayId.SiteId.Value,
                    right.ProductionDayId.SiteId.Value),
            _ => throw new ArgumentException(
                "Reporting evaluation keys from different period scopes cannot be ordered together."),
        };

        private static int CompareShiftIdentity(
            ShiftOccurrenceId x,
            ShiftOccurrenceId y)
        {
            var comparison = StringComparer.Ordinal.Compare(x.SiteId.Value, y.SiteId.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(
                x.ShiftScheduleAssignmentId.Value,
                y.ShiftScheduleAssignmentId.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(x.ShiftId.Value, y.ShiftId.Value);
            return comparison != 0
                ? comparison
                : x.EndsAtUtc.CompareTo(y.EndsAtUtc);
        }

        private static int CompareContext(
            OperationalMetricEvaluationContextKey x,
            OperationalMetricEvaluationContextKey y)
        {
            var comparison = CompareOptional(x.ProductionOrderId?.Value, y.ProductionOrderId?.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareOptional(x.OperationId?.Value, y.OperationId?.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareOptional(x.PartId?.Value, y.PartId?.Value);
            return comparison != 0
                ? comparison
                : CompareOptional(x.OperatorId?.Value, y.OperatorId?.Value);
        }

        private static int CompareOptional(string? x, string? y)
        {
            if (x is null)
            {
                return y is null ? 0 : -1;
            }

            return y is null ? 1 : StringComparer.Ordinal.Compare(x, y);
        }
    }
}

public sealed record ReportingMachineSelection
{
    public ReportingMachineSelection(IEnumerable<MachineId> machineIds)
    {
        ArgumentNullException.ThrowIfNull(machineIds);

        var snapshot = machineIds.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("At least one machine ID is required.", nameof(machineIds));
        }

        if (snapshot.Any(static machineId => machineId.IsEmpty))
        {
            throw new ArgumentException("Machine IDs cannot be empty.", nameof(machineIds));
        }

        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("Machine IDs cannot contain duplicates.", nameof(machineIds));
        }

        MachineIds = new ReadOnlyCollection<MachineId>(snapshot);
    }

    public IReadOnlyList<MachineId> MachineIds { get; }
}

public sealed record OperationalMetricDefinitionSelection
{
    public OperationalMetricDefinitionSelection(
        IEnumerable<OperationalMetricDefinitionId> definitionIds)
    {
        ArgumentNullException.ThrowIfNull(definitionIds);

        var snapshot = definitionIds.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one exact metric definition ID is required.",
                nameof(definitionIds));
        }

        if (snapshot.Any(static definitionId => definitionId is null))
        {
            throw new ArgumentException(
                "Metric definition IDs cannot contain null values.",
                nameof(definitionIds));
        }

        var duplicate = snapshot
            .GroupBy(static definitionId => definitionId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Metric definition selection cannot contain duplicate definition '{duplicate.Key.MetricKey}/{duplicate.Key.Version}'.",
                nameof(definitionIds));
        }

        DefinitionIds = new ReadOnlyCollection<OperationalMetricDefinitionId>(snapshot);
    }

    public IReadOnlyList<OperationalMetricDefinitionId> DefinitionIds { get; }
}

public sealed record OperationalMetricStatusSelection
{
    public OperationalMetricStatusSelection(
        IEnumerable<OperationalMetricEvaluationStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var snapshot = statuses.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one evaluation status is required.",
                nameof(statuses));
        }

        if (snapshot.Any(static status => !Enum.IsDefined(status)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(statuses),
                "Evaluation status selection contains an unsupported value.");
        }

        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Evaluation statuses cannot contain duplicates.",
                nameof(statuses));
        }

        Statuses = new ReadOnlyCollection<OperationalMetricEvaluationStatus>(snapshot);
    }

    public IReadOnlyList<OperationalMetricEvaluationStatus> Statuses { get; }
}

public sealed record OperationalMetricContextFilter
{
    public ProductionOrderId? ProductionOrderId { get; init; }

    public OperationId? OperationId { get; init; }

    public PartId? PartId { get; init; }

    public OperatorId? OperatorId { get; init; }

    public void Validate()
    {
        if (ProductionOrderId is { IsEmpty: true })
        {
            throw new ArgumentException(
                "Production order ID cannot be empty when specified.",
                nameof(ProductionOrderId));
        }

        if (OperationId is { IsEmpty: true })
        {
            throw new ArgumentException(
                "Operation ID cannot be empty when specified.",
                nameof(OperationId));
        }

        if (PartId is { IsEmpty: true })
        {
            throw new ArgumentException(
                "Part ID cannot be empty when specified.",
                nameof(PartId));
        }

        if (OperatorId is { IsEmpty: true })
        {
            throw new ArgumentException(
                "Operator ID cannot be empty when specified.",
                nameof(OperatorId));
        }
    }

    public bool Matches(OperationalMetricEvaluationContextKey contextKey)
    {
        ArgumentNullException.ThrowIfNull(contextKey);
        Validate();
        contextKey.Validate();

        return (ProductionOrderId is null || ProductionOrderId == contextKey.ProductionOrderId) &&
            (OperationId is null || OperationId == contextKey.OperationId) &&
            (PartId is null || PartId == contextKey.PartId) &&
            (OperatorId is null || OperatorId == contextKey.OperatorId);
    }
}

public sealed record ReportingContinuationToken
{
    public ReportingContinuationToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ReportingPageRequest
{
    public const int MaximumPageSize = 200;

    public ReportingPageRequest(
        int pageSize,
        ReportingContinuationToken? continuationToken = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);

        PageSize = pageSize;
        ContinuationToken = continuationToken;
    }

    public int PageSize { get; }

    public ReportingContinuationToken? ContinuationToken { get; }
}

public sealed record ReportingPage<T>
{
    public ReportingPage(
        IEnumerable<T> items,
        ReportingContinuationToken? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var snapshot = items.ToArray();
        if (snapshot.Any(static item => item is null))
        {
            throw new ArgumentException("Reporting pages cannot contain null items.", nameof(items));
        }

        Items = new ReadOnlyCollection<T>(snapshot);
        ContinuationToken = continuationToken;
    }

    public IReadOnlyList<T> Items { get; }

    public ReportingContinuationToken? ContinuationToken { get; }
}

public abstract record OperationalMetricReportQuery
{
    private protected OperationalMetricReportQuery(
        OperationalMetricProjectionProcessorId processorId,
        ReportingMachineSelection machines,
        OperationalMetricDefinitionSelection? metrics,
        OperationalMetricContextFilter? context,
        OperationalMetricStatusSelection? statuses,
        OperationalMetricReportOrder order,
        ReportingPageRequest page)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(page);

        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        context?.Validate();

        ProcessorId = processorId;
        Machines = machines;
        Metrics = metrics;
        Context = context;
        Statuses = statuses;
        Order = order;
        Page = page;
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public ReportingMachineSelection Machines { get; }

    public OperationalMetricDefinitionSelection? Metrics { get; }

    public OperationalMetricContextFilter? Context { get; }

    public OperationalMetricStatusSelection? Statuses { get; }

    public OperationalMetricReportOrder Order { get; }

    public ReportingPageRequest Page { get; }
}

public sealed record ShiftOperationalMetricReportQuery : OperationalMetricReportQuery
{
    public ShiftOperationalMetricReportQuery(
        OperationalMetricProjectionProcessorId processorId,
        ReportingMachineSelection machines,
        DateTimeOffset startsAtOrAfterUtc,
        DateTimeOffset startsBeforeUtc,
        OperationalMetricDefinitionSelection? metrics,
        OperationalMetricContextFilter? context,
        OperationalMetricStatusSelection? statuses,
        OperationalMetricReportOrder order,
        ReportingPageRequest page)
        : base(processorId, machines, metrics, context, statuses, order, page)
    {
        if (startsAtOrAfterUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Shift report range start must use a zero UTC offset.",
                nameof(startsAtOrAfterUtc));
        }

        if (startsBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Shift report range end must use a zero UTC offset.",
                nameof(startsBeforeUtc));
        }

        if (startsBeforeUtc <= startsAtOrAfterUtc)
        {
            throw new ArgumentException(
                "Shift report range end must be after its start.",
                nameof(startsBeforeUtc));
        }

        StartsAtOrAfterUtc = startsAtOrAfterUtc;
        StartsBeforeUtc = startsBeforeUtc;
    }

    public DateTimeOffset StartsAtOrAfterUtc { get; }

    public DateTimeOffset StartsBeforeUtc { get; }
}

public sealed record ProductionDayOperationalMetricReportQuery : OperationalMetricReportQuery
{
    public ProductionDayOperationalMetricReportQuery(
        OperationalMetricProjectionProcessorId processorId,
        ReportingMachineSelection machines,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        OperationalMetricDefinitionSelection? metrics,
        OperationalMetricContextFilter? context,
        OperationalMetricStatusSelection? statuses,
        OperationalMetricReportOrder order,
        ReportingPageRequest page)
        : base(processorId, machines, metrics, context, statuses, order, page)
    {
        if (fromInclusive == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromInclusive),
                "Production-day report range start is required.");
        }

        if (toExclusive == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toExclusive),
                "Production-day report range end is required.");
        }

        if (toExclusive <= fromInclusive)
        {
            throw new ArgumentException(
                "Production-day report range end must be after its start.",
                nameof(toExclusive));
        }

        FromInclusive = fromInclusive;
        ToExclusive = toExclusive;
    }

    public DateOnly FromInclusive { get; }

    public DateOnly ToExclusive { get; }
}
