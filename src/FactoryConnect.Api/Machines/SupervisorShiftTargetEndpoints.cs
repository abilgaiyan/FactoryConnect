using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api.Machines;

public sealed record LocalShiftTargetRequest(
    Guid TargetRevisionId, string ShiftScheduleAssignmentId, string ShiftId,
    DateTimeOffset ShiftStartsAtUtc, DateTimeOffset ShiftEndsAtUtc,
    int RevisionNumber, Guid? ExpectedHeadRevisionId, decimal TargetQuantity,
    string QuantityUnit, string? ProductionOrder, string? PartNumber,
    string? Operation, string? RevisionReason);

/// <summary>Local commissioning-only planning target endpoints; not OEE publication.</summary>
public static class SupervisorShiftTargetEndpoints
{
    public static IEndpointRouteBuilder MapSupervisorShiftTargetEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/shift-targets",
            ReadAsync).WithName("ReadLocalSupervisorShiftTargets")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapPost("/api/supervisor/v1/machines/{machineId:guid}/shift-targets",
            CreateAsync).WithName("CreateLocalSupervisorShiftTargetRevision")
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
            var revisions = await new SqlServerSupervisorShiftTargetStore(connectionString)
                .ListAsync(machineId, assignmentId, shiftStartsAtUtc, shiftEndsAtUtc,
                    cancellationToken);
            return Results.Ok(revisions);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidShiftTargetIdentity" });
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Shift target SQL read unavailable");
        }
    }

    private static async Task<IResult> CreateAsync(Guid machineId, LocalShiftTargetRequest request,
        HttpContext context, IConfiguration configuration, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("SupervisorLocalShiftTarget:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local shift target writes disabled");
        var actor = configuration["SupervisorLocalShiftTarget:OperatorId"];
        var connectionString = configuration["SupervisorLocalShiftTarget:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 ||
            string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Local shift target writer not configured");
        var revision = new SupervisorShiftTargetRevision(
            request.TargetRevisionId, machineId, request.ShiftScheduleAssignmentId,
            request.ShiftId, request.ShiftStartsAtUtc, request.ShiftEndsAtUtc,
            request.RevisionNumber, request.TargetQuantity, request.QuantityUnit,
            request.ProductionOrder, request.PartNumber, request.Operation,
            request.RevisionReason, actor, clock.GetUtcNow());
        try
        {
            var inserted = await new SqlServerSupervisorShiftTargetStore(connectionString)
                .AppendAsync(revision, request.ExpectedHeadRevisionId, cancellationToken);
            return inserted
                ? Results.Created($"/api/supervisor/v1/machines/{machineId:D}/shift-targets",
                    revision)
                : Results.Ok(new { revisionId = request.TargetRevisionId, alreadyExists = true });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidShiftTarget" });
        }
        catch (SqlException exception) when (exception.Number is 51100 or 51101 or 51102 or 51103 or 2601 or 2627)
        {
            return Results.Problem(statusCode: 409, title: "Shift target roster or revision conflict");
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Shift target SQL write unavailable");
        }
    }
}
