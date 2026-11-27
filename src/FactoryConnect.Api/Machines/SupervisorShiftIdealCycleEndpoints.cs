using System.Security.Claims;
using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api.Machines;

public sealed record LocalShiftIdealCycleRequest(
    Guid CycleRevisionId, string ShiftScheduleAssignmentId, string ShiftId,
    DateTimeOffset ShiftStartsAtUtc, DateTimeOffset ShiftEndsAtUtc,
    int RevisionNumber, Guid? ExpectedHeadRevisionId, decimal IdealCycleSeconds,
    string? ProductionOrder, string? PartNumber, string? Operation, string? RevisionReason);

public sealed record LocalCycleReviewRequest(Guid ReviewId, Guid CycleRevisionId,
    string AssignmentId, string ShiftId, DateTimeOffset ShiftStartsAtUtc,
    DateTimeOffset ShiftEndsAtUtc, bool? IsAccepted);

public static class SupervisorShiftIdealCycleEndpoints
{
    public static IEndpointRouteBuilder MapSupervisorShiftIdealCycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/ideal-cycles", ReadAsync)
            .RequireAuthorization("OperationalRead").WithName("ReadLocalSupervisorShiftIdealCycles")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapPost("/api/supervisor/v1/machines/{machineId:guid}/ideal-cycles", CreateAsync)
            .RequireAuthorization("SupervisorWrite").WithName("CreateLocalSupervisorShiftIdealCycleRevision")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/ideal-cycle-reviews", ReadCycleReviewsAsync)
            .RequireAuthorization("OperationalRead").WithName("ReadLocalSupervisorCycleReviews")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapPost("/api/supervisor/v1/machines/{machineId:guid}/ideal-cycle-reviews", ReviewCycleAsync)
            .RequireAuthorization("SupervisorWrite").WithName("CreateLocalSupervisorCycleReview")
            .WithGroupName("supervisor-local-v1");
        return endpoints;
    }

    private static async Task<IResult> ReadCycleReviewsAsync(Guid machineId, string assignmentId,
        string shiftId, DateTimeOffset shiftStartsAtUtc, DateTimeOffset shiftEndsAtUtc,
        HttpContext context, IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Commissioning SQL reader not configured");
        try
        {
            var reviews = await new SqlServerSupervisorCycleReviewStore(connectionString)
                .ListAsync(machineId, assignmentId, shiftId, shiftStartsAtUtc, shiftEndsAtUtc,
                    clock.GetUtcNow(), cancellationToken);
            return Results.Ok(reviews);
        }
        catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidCycleReviewScope" }); }
        catch (SqlException) { return Results.Problem(statusCode: 503, title: "Cycle review SQL read unavailable"); }
    }

    private static async Task<IResult> ReviewCycleAsync(Guid machineId, LocalCycleReviewRequest request,
        HttpContext context, IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("SupervisorLocalCycleReview:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local cycle review writes disabled");
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var connectionString = configuration["SupervisorLocalCycleReview:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Local cycle review writer not configured");
        if (request.IsAccepted is null || request.ReviewId == Guid.Empty || request.CycleRevisionId == Guid.Empty ||
            machineId == Guid.Empty || string.IsNullOrWhiteSpace(request.AssignmentId) || request.AssignmentId.Length > 200 ||
            string.IsNullOrWhiteSpace(request.ShiftId) || request.ShiftId.Length > 200 ||
            request.ShiftStartsAtUtc.Offset != TimeSpan.Zero || request.ShiftEndsAtUtc.Offset != TimeSpan.Zero ||
            request.ShiftEndsAtUtc <= request.ShiftStartsAtUtc ||
            request.ShiftEndsAtUtc - request.ShiftStartsAtUtc > TimeSpan.FromDays(1))
            return Results.BadRequest(new { reason = "InvalidCycleReview" });
        try
        {
            // Revision identity and scope are immutable; verify against the same database used for append.
            var revisions = await new SqlServerSupervisorShiftIdealCycleStore(connectionString)
                .ListAsync(machineId, request.AssignmentId, request.ShiftStartsAtUtc, request.ShiftEndsAtUtc, cancellationToken);
            if (!revisions.Any(r => r.CycleRevisionId == request.CycleRevisionId &&
                r.MachineId == machineId && r.ShiftScheduleAssignmentId == request.AssignmentId &&
                r.ShiftId == request.ShiftId && r.ShiftStartsAtUtc == request.ShiftStartsAtUtc &&
                r.ShiftEndsAtUtc == request.ShiftEndsAtUtc))
                return Results.Problem(statusCode: 409, title: "Cycle revision does not belong to requested shift");
            var review = new SupervisorCycleReview(request.ReviewId, request.CycleRevisionId,
                request.IsAccepted.Value ? SupervisorCycleReviewDecision.Accepted : SupervisorCycleReviewDecision.Rejected,
                actor, clock.GetUtcNow());
            var inserted = await new SqlServerSupervisorCycleReviewStore(connectionString).AppendAsync(review, cancellationToken);
            return Results.Ok(new { reviewId = review.ReviewId, alreadyExists = !inserted });
        }
        catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidCycleReview" }); }
        catch (SqlException ex) when (ex.Number is 51144 or 51145 or 2601 or 2627)
        { return Results.Problem(statusCode: 409, title: "Cycle review conflict"); }
        catch (SqlException) { return Results.Problem(statusCode: 503, title: "Cycle review SQL write unavailable"); }
    }

    private static async Task<IResult> ReadAsync(Guid machineId, string assignmentId,
        DateTimeOffset shiftStartsAtUtc, DateTimeOffset shiftEndsAtUtc,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Commissioning SQL reader not configured");
        try
        {
            var revisions = await new SqlServerSupervisorShiftIdealCycleStore(connectionString)
                .ListAsync(machineId, assignmentId, shiftStartsAtUtc, shiftEndsAtUtc,
                    cancellationToken);
            return Results.Ok(revisions);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidShiftIdealCycleIdentity" });
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Ideal cycle SQL read unavailable");
        }
    }

    private static async Task<IResult> CreateAsync(Guid machineId, LocalShiftIdealCycleRequest request,
        HttpContext context, IConfiguration configuration, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("SupervisorLocalShiftIdealCycle:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local ideal cycle writes disabled");
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var connectionString = configuration["SupervisorLocalShiftIdealCycle:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 ||
            string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Local ideal cycle writer not configured");
        var revision = new SupervisorShiftIdealCycleRevision(
            request.CycleRevisionId, machineId, request.ShiftScheduleAssignmentId,
            request.ShiftId, request.ShiftStartsAtUtc, request.ShiftEndsAtUtc,
            request.RevisionNumber, request.IdealCycleSeconds,
            request.ProductionOrder, request.PartNumber, request.Operation,
            request.RevisionReason, actor, clock.GetUtcNow());
        try
        {
            var inserted = await new SqlServerSupervisorShiftIdealCycleStore(connectionString)
                .AppendAsync(revision, request.ExpectedHeadRevisionId, cancellationToken);
            return inserted
                ? Results.Created($"/api/supervisor/v1/machines/{machineId:D}/ideal-cycles",
                    revision)
                : Results.Ok(new { revisionId = request.CycleRevisionId, alreadyExists = true });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidShiftIdealCycle" });
        }
        catch (SqlException exception) when (exception.Number is 51140 or 51141 or 51142 or 51143 or 2601 or 2627)
        {
            return Results.Problem(statusCode: 409, title: "Ideal cycle roster or revision conflict");
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Ideal cycle SQL write unavailable");
        }
    }
}
