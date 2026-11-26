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
        return endpoints;
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
            request.RevisionNumber, request.IdealCycleSeconds, request.QuantityUnit,
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
