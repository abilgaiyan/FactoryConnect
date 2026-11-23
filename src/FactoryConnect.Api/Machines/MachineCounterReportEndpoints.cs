using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Api.Machines;

public sealed record MachineCounterReportRequest(DateTimeOffset FromInclusive, DateTimeOffset ToExclusive,
    string Source, string Address, string? CandidateDataItemId = null);

public static class MachineCounterReportEndpoints
{
    public static IEndpointRouteBuilder MapMachineCounterReports(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/machines/v1/{machineId}/counter/query", ReadAsync)
            .WithName("QueryMachineCounterReport").WithGroupName("counter-v1")
            .Produces<MachineCounterReport>().ProducesProblem(503).ProducesProblem(422);
        endpoints.MapPost("/api/commissioning/v1/machines/{machineId}/counter/profile-query", ReadProfileBackedAsync)
            .WithName("QueryLocalProfileBackedCounterReport").WithGroupName("commissioning-local-v1")
            .Produces<MachineCounterReport>().ProducesProblem(403).ProducesProblem(503).ProducesProblem(422);
        return endpoints;
    }

    private static async Task<IResult> ReadProfileBackedAsync(string machineId, MachineCounterReportRequest request,
        HttpContext context, IConfiguration configuration,
        ICurrentStateOwnerResolver owners, ICurrentStateAuthorityCutProvider cuts,
        IDurableObservationReader reader, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        if (!Guid.TryParseExact(machineId, "D", out var id) || id == Guid.Empty ||
            request.FromInclusive.Offset != TimeSpan.Zero || request.ToExclusive.Offset != TimeSpan.Zero ||
            request.ToExclusive <= request.FromInclusive || request.ToExclusive - request.FromInclusive > TimeSpan.FromDays(31) ||
            string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Address) ||
            request.Source.Length > 256 || request.Address.Length > 256 ||
            request.CandidateDataItemId is not null)
            return Results.BadRequest(new { reason = "InvalidLocalCounterProfileQuery" });

        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Machine profile reader not configured");

        try
        {
            var profiles = await new SqlServerMachineProductionEvidenceProfileStore(connectionString)
                .ListAsync(id, cancellationToken).ConfigureAwait(false);
            var profile = CounterProfileIntervalGuard.EffectiveUnchanged(
                profiles, request.FromInclusive, request.ToExclusive);
            if (profile is null)
                return Results.Problem(statusCode: 422, title: "No unchanged counter profile across requested interval");
            if (string.IsNullOrWhiteSpace(profile.CounterDataItemId))
                return Results.Problem(statusCode: 422, title: "Effective machine profile has no counter configured");
            // SQL configuration is a candidate for raw address comparison, not verified identity.
            return await ReadAsync(machineId, request with { CandidateDataItemId = profile.CounterDataItemId },
                owners, cuts, reader, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidMachineEvidenceProfile" });
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Machine profile reader unavailable");
        }
    }

    private static async Task<IResult> ReadAsync(string machineId, MachineCounterReportRequest request,
        ICurrentStateOwnerResolver owners, ICurrentStateAuthorityCutProvider cuts,
        IDurableObservationReader reader, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(machineId, "D", out var id) || id == Guid.Empty ||
            request.FromInclusive.Offset != TimeSpan.Zero || request.ToExclusive.Offset != TimeSpan.Zero ||
            request.ToExclusive <= request.FromInclusive || request.ToExclusive - request.FromInclusive > TimeSpan.FromDays(31) ||
            string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Address) ||
            request.Source.Length > 256 || request.Address.Length > 256 ||
            (request.CandidateDataItemId is not null &&
             (string.IsNullOrWhiteSpace(request.CandidateDataItemId) || request.CandidateDataItemId.Length > 256)))
            return Results.BadRequest(new { reason = "InvalidMachineIntervalOrExactSignal" });
        if (owners.Resolve(new MachineId(id)) is not CurrentStateExactlyOneOwner owner)
            return Results.Problem(statusCode: 503, title: "Counter stream owner unavailable");
        if (owner.Binding.MachineId != new MachineId(id)) throw new InvalidDataException("Counter owner machine mismatch.");
        var cut = await cuts.ReadAuthorityCutAsync(owner.Binding, cancellationToken).ConfigureAwait(false);
        if (cut is not StableCurrentStateAuthorityCut stable)
            return Results.Problem(statusCode: 503, title: "Stable counter read boundary unavailable");
        if (stable.Cut.Binding != owner.Binding) throw new InvalidDataException("Counter binding mismatch.");
        var acquisition = stable.Cut.AcquisitionContact;
        if (acquisition is null || acquisition.RawAcceptedThrough is null)
            return Results.Problem(statusCode: 503, title: "Raw acquisition boundary not established");
        var stream = owner.Binding.ObservationStreamId;
        if (acquisition.ObservationStreamId != stream) throw new InvalidDataException("Counter acquisition stream mismatch.");

        const int batchSize = 1000;
        const int maximumObservations = 1000000;
        var observations = new List<DurableMachineObservation>();
        ObservationPosition? after = null;
        var through = acquisition.RawAcceptedThrough;
        var reached = false;
        while (!reached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await reader.ReadAsync(new(stream, after, batchSize), cancellationToken).ConfigureAwait(false);
            if (page.StreamId != stream || page.Observations.Count > batchSize ||
                (page.HasMore && page.Observations.Count == 0)) throw new InvalidDataException("Invalid raw observation page.");
            foreach (var item in page.Observations)
            {
                if (item.StreamId != stream || item.Position <= after) throw new InvalidDataException("Raw counter pagination mismatch.");
                if (item.Position > through) throw new InvalidDataException("Pinned raw boundary was omitted.");
                after = item.Position;
                if (observations.Count == maximumObservations)
                    return Results.Problem(statusCode: 422, title: "Counter traversal limit exceeded", detail: "No partial report was produced.");
                observations.Add(item);
                if (item.Position == through) { reached = true; break; }
            }
            if (!reached && !page.HasMore) throw new InvalidDataException("Raw history ended before the pinned boundary.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var report = MachineCounterReportComposer.ComposeWithIdentityAssessment(stream, through, acquisition.AcquisitionRevision,
            request.FromInclusive, request.ToExclusive, clock.GetUtcNow(), request.Source, request.Address,
            request.CandidateDataItemId, observations);
        if (report.Samples.Count > 5000)
            return Results.Problem(statusCode: 422, title: "Selected counter sample limit exceeded", detail: "Select a smaller range. No partial report was produced.");
        return Results.Ok(report);
    }
}
