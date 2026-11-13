using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Api.Machines;

/// <summary>Local commissioning access to supervisor declarations; never publishes OEE.</summary>
public sealed record LocalSupervisorEntryCreateRequest(Guid EntryId, string ShiftScheduleAssignmentId,
    string ShiftId, DateTimeOffset ShiftStartsAtUtc, DateTimeOffset ShiftEndsAtUtc,
    string ProductionOrder, string PartNumber, string Operation, string? Note,
    long? GoodQuantity = null, long? RejectedQuantity = null);
public sealed record LocalSupervisorReviewRequest(Guid DecisionId, string Decision, string Reason);

public static class SupervisorProductionEntryEndpoints
{
    public static IEndpointRouteBuilder MapSupervisorProductionEntryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/entries", ReadAsync)
            .WithName("ReadSupervisorProductionEntries")
            .WithGroupName("supervisor-local-v1")
            .Produces<IReadOnlyList<SupervisorProductionEntryRecord>>()
            .ProducesProblem(403)
            .ProducesProblem(503);
        endpoints.MapPost("/api/supervisor/v1/machines/{machineId:guid}/entries", CreateAsync)
            .WithName("CreateLocalSupervisorProductionEntry")
            .WithGroupName("supervisor-local-v1")
            .Produces<SupervisorProductionEntryRecord>(201)
            .ProducesProblem(403)
            .ProducesProblem(409)
            .ProducesProblem(503);
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/reviews", ReadReviewsAsync)
            .WithName("ReadLocalSupervisorReviews")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapPost("/api/supervisor/v1/machines/{machineId:guid}/entries/{entryId:guid}/review", ReviewAsync)
            .WithName("DecideLocalSupervisorReview")
            .WithGroupName("supervisor-local-v1");
        return endpoints;
    }


    private static async Task<IResult> CreateAsync(
        Guid machineId, LocalSupervisorEntryCreateRequest request,
        HttpContext context, IConfiguration configuration, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // Deliberately restricted to an explicitly enabled local commissioning session.
        // This is not a factory-network or authenticated supervisor write endpoint.
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("SupervisorLocalEntry:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local supervisor writes are disabled");

        var actor = configuration["SupervisorLocalEntry:OperatorId"];
        var connectionString = configuration["SupervisorLocalEntry:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 ||
            string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Local supervisor writer not configured");

        if (machineId == Guid.Empty || request.EntryId == Guid.Empty ||
            request.ShiftStartsAtUtc.Offset != TimeSpan.Zero ||
            request.ShiftEndsAtUtc.Offset != TimeSpan.Zero ||
            request.ShiftEndsAtUtc <= request.ShiftStartsAtUtc ||
            request.ShiftEndsAtUtc - request.ShiftStartsAtUtc > TimeSpan.FromHours(24))
            return Results.BadRequest(new { reason = "InvalidEntryIdentityOrUtcShift" });

        var entry = new SupervisorProductionEntryRecord(request.EntryId, machineId,
            request.ShiftScheduleAssignmentId, request.ShiftId,
            request.ShiftStartsAtUtc, request.ShiftEndsAtUtc,
            request.ProductionOrder, request.PartNumber, request.Operation,
            request.Note, actor, clock.GetUtcNow(), request.GoodQuantity, request.RejectedQuantity);
        try
        {
            var inserted = await new SqlServerSupervisorProductionEntryWriter(connectionString)
                .AppendAsync(entry, cancellationToken);
            if (inserted)
                return Results.Created($"/api/supervisor/v1/machines/{machineId:D}/entries", entry);

            // The original timestamp remains durable in SQL; do not fabricate it.
            return Results.Ok(new { entryId = request.EntryId, alreadyExists = true });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidSupervisorEntry" });
        }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number is 51024 or 51025)
        {
            return Results.Problem(statusCode: 409, title: "Supervisor entry conflicts with roster or existing entry");
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Supervisor entry SQL write unavailable");
        }
    }

    private static async Task<IResult> ReadAsync(
        Guid machineId, DateTimeOffset shiftStartsAtUtc, DateTimeOffset shiftEndsAtUtc,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        // No authenticated supervisor access model exists in V1 yet. Keep this local-only.
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");

        if (machineId == Guid.Empty ||
            shiftStartsAtUtc.Offset != TimeSpan.Zero || shiftEndsAtUtc.Offset != TimeSpan.Zero ||
            shiftStartsAtUtc >= shiftEndsAtUtc ||
            shiftEndsAtUtc - shiftStartsAtUtc > TimeSpan.FromHours(24))
            return Results.BadRequest(new { reason = "InvalidMachineOrUtcShift" });

        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Commissioning SQL reader not configured");

        var reader = new SqlServerSupervisorProductionEntryReader(connectionString);
        try
        {
            return Results.Ok(await reader.ListAsync(machineId, shiftStartsAtUtc, shiftEndsAtUtc,
                cancellationToken: cancellationToken));
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Supervisor entry SQL read unavailable");
        }
    }
    private static async Task<IResult> ReviewAsync(Guid machineId, Guid entryId,
        LocalSupervisorReviewRequest request, HttpContext context, IConfiguration configuration,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("SupervisorLocalReview:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local review decisions are disabled");
        var reviewer = configuration["SupervisorLocalReview:ReviewerId"];
        var connectionString = configuration["SupervisorLocalReview:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 200 ||
            string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Local reviewer not configured");
        if (machineId == Guid.Empty || entryId == Guid.Empty || request.DecisionId == Guid.Empty)
            return Results.BadRequest(new { reason = "InvalidReviewIdentity" });
        try
        {
            // Bind the decision to the machine path, not just a client-supplied entry ID.
            var store = new SqlServerSupervisorProductionEntryReviewStore(connectionString);
            // Machine membership is checked in the transactional decision query.
            var inserted = await store.DecideAsync(machineId, entryId, request.DecisionId,
                request.Decision, request.Reason, reviewer, clock.GetUtcNow(), cancellationToken);
            return inserted ? Results.Created($"/api/supervisor/v1/machines/{machineId:D}/reviews",
                new { entryId, request.DecisionId, request.Decision }) :
                Results.Ok(new { entryId, request.DecisionId, alreadyExists = true });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidReviewDecision" });
        }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number is 51026 or 51027 or 51028)
        {
            return Results.Problem(statusCode: 409, title: "Review target, author or prior decision conflict");
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Local review SQL unavailable");
        }
    }

    private static async Task<IResult> ReadReviewsAsync(Guid machineId,
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
            return Results.Ok(await new SqlServerSupervisorProductionEntryReviewStore(connectionString)
                .ListAsync(machineId, shiftStartsAtUtc, shiftEndsAtUtc, cancellationToken));
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidMachineOrUtcShift" });
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Local review SQL unavailable");
        }
    }

}
