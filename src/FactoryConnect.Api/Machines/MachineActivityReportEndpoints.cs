using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Api.Machines;

public sealed record MachineActivityReportRequest(DateTimeOffset FromInclusive, DateTimeOffset ToExclusive);

public static class MachineActivityReportEndpoints
{
    // A bounded full-prefix traversal. A limit or read failure returns no report.
    private const int BatchSize = 1000;
    private const int MaximumPeriods = 100000;

    public static IEndpointRouteBuilder MapMachineActivityReports(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/machines/v1/{machineId}/activity/query", ReadAsync)
            .WithName("QueryMachineActivityReport")
            .WithGroupName("activity-v1")
            .Produces<MachineActivityReport>()
            .ProducesProblem(503)
            .ProducesProblem(422);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        string machineId, MachineActivityReportRequest request,
        ICurrentStateOwnerResolver owners, ICurrentStateAuthorityCutProvider cuts,
        IProductionContextActivityReader reader, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(machineId, "D", out var id) || id == Guid.Empty ||
            request.FromInclusive.Offset != TimeSpan.Zero || request.ToExclusive.Offset != TimeSpan.Zero ||
            request.ToExclusive <= request.FromInclusive ||
            request.ToExclusive - request.FromInclusive > TimeSpan.FromDays(31))
            return Results.BadRequest(new { reason = "InvalidMachineOrUtcInterval", maximumDays = 31 });

        if (owners.Resolve(new MachineId(id)) is not CurrentStateExactlyOneOwner owner)
            return Results.Problem(statusCode: 503, title: "Machine activity owner unavailable");
        if (owner.Binding.MachineId != new MachineId(id))
            throw new InvalidDataException("Activity owner returned a different machine.");
        // The selected provider's existing activity reader is bound to this processor.
        if (owner.Binding.StateProcessorId != new ObservationProcessorId("machine-state-activity"))
            return Results.Problem(statusCode: 503, title: "Unsupported activity owner");
        var result = await cuts.ReadAuthorityCutAsync(owner.Binding, cancellationToken).ConfigureAwait(false);
        if (result is not StableCurrentStateAuthorityCut stable)
            return Results.Problem(statusCode: 503, title: "Stable machine activity cut unavailable");
        if (stable.Cut.Binding != owner.Binding ||
            (stable.Cut.Evaluation is { } evaluation &&
             (evaluation.StateProcessorId != owner.Binding.StateProcessorId ||
              evaluation.ObservationStreamId != owner.Binding.ObservationStreamId)))
            throw new InvalidDataException("Activity cut disagrees with selected owner.");

        var through = stable.Cut.Evaluation?.EvaluatedThrough;
        var periods = new List<DurableMachineActivityPeriod>();
        ObservationPosition? after = null;
        if (through is not null)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await reader.ReadAsync(owner.Binding.ObservationStreamId, after,
                    BatchSize, cancellationToken).ConfigureAwait(false);
                if (page.Count > BatchSize) throw new InvalidDataException("Activity reader exceeded its page limit.");
                var finished = page.Count < BatchSize;
                foreach (var item in page)
                {
                    if (item.ProcessorId != owner.Binding.StateProcessorId ||
                        item.StreamId != owner.Binding.ObservationStreamId || item.Position <= after)
                        throw new InvalidDataException("Activity pagination identity or ordering mismatch.");
                    after = item.Position;
                    if (item.Position > through) { finished = true; break; }
                    if (periods.Count == MaximumPeriods)
                        return Results.Problem(statusCode: 422, title: "Activity traversal limit exceeded",
                            detail: "No report was produced. A date-indexed reader is required for this history size.");
                    periods.Add(item);
                }
                if (finished || after >= through) break;
            }
        }
        var report = MachineActivityReportComposer.Compose(owner.Binding, through,
            request.FromInclusive, request.ToExclusive, clock.GetUtcNow(), periods);
        if (report.Intervals.Count > 5000)
            return Results.Problem(statusCode: 422, title: "Selected activity interval limit exceeded",
                detail: "Select a smaller date range. No report was produced.");
        return Results.Ok(report);
    }
}
