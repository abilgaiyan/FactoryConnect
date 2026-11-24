using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api.Machines;

public sealed record LocalMachineEvidenceProfileRequest(
    Guid ProfileRevisionId, int RevisionNumber, Guid? ExpectedHeadRevisionId,
    string EvidenceMode, string? StateDataItemId, string? CounterDataItemId,
    string? CounterSemantics, DateTimeOffset EffectiveFromUtc);

/// <summary>Explicitly gated local commissioning configuration, never quantity publication.</summary>
public static class MachineEvidenceProfileEndpoints
{
    public static IEndpointRouteBuilder MapMachineEvidenceProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/commissioning/v1/machines/{machineId:guid}/evidence-profiles",
            ListAsync).RequireAuthorization("OperationalRead").WithName("ListLocalMachineEvidenceProfiles").WithGroupName("commissioning-local-v1");
        endpoints.MapGet("/api/commissioning/v1/machines/{machineId:guid}/evidence-profile",
            ReadEffectiveAsync).RequireAuthorization("OperationalRead").WithName("ReadLocalMachineEvidenceProfile").WithGroupName("commissioning-local-v1");
        endpoints.MapPost("/api/commissioning/v1/machines/{machineId:guid}/evidence-profiles",
            AppendAsync).RequireAuthorization("AdministratorWrite").WithName("AppendLocalMachineEvidenceProfile").WithGroupName("commissioning-local-v1");
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid machineId, HttpContext context,
        IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Machine profile reader not configured");
        try
        {
            return Results.Ok(await new SqlServerMachineProductionEvidenceProfileStore(connectionString)
                .ListAsync(machineId, cancellationToken));
        }
        catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidMachineId" }); }
        catch (SqlException) { return Results.Problem(statusCode: 503, title: "Machine profile reader unavailable"); }
    }

    private static async Task<IResult> ReadEffectiveAsync(Guid machineId, DateTimeOffset atUtc,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Machine profile reader not configured");
        try
        {
            var profile = await new SqlServerMachineProductionEvidenceProfileStore(connectionString)
                .GetEffectiveAsync(machineId, atUtc, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        }
        catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidMachineOrUtcInstant" }); }
        catch (SqlException) { return Results.Problem(statusCode: 503, title: "Machine profile reader unavailable"); }
    }

    private static async Task<IResult> AppendAsync(Guid machineId, LocalMachineEvidenceProfileRequest request,
        HttpContext context, IConfiguration configuration, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("MachineEvidenceProfileLocal:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local machine profile writes disabled");
        var connectionString = configuration["MachineEvidenceProfileLocal:WriteConnectionString"];
        var actor = configuration["MachineEvidenceProfileLocal:OperatorId"];
        if (string.IsNullOrWhiteSpace(connectionString) ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 200)
            return Results.Problem(statusCode: 503, title: "Machine profile writer not configured");
        var profile = new MachineProductionEvidenceProfile(
            request.ProfileRevisionId, machineId, request.RevisionNumber, request.EvidenceMode,
            request.StateDataItemId, request.CounterDataItemId, request.CounterSemantics,
            request.EffectiveFromUtc, actor, clock.GetUtcNow());
        try
        {
            var inserted = await new SqlServerMachineProductionEvidenceProfileStore(connectionString)
                .AppendAsync(profile, request.ExpectedHeadRevisionId, cancellationToken);
            return inserted
                ? Results.Created($"/api/commissioning/v1/machines/{machineId:D}/evidence-profiles", profile)
                : Results.Ok(profile);
        }
        catch (ArgumentException) { return Results.BadRequest(new { reason = "InvalidMachineEvidenceProfile" }); }
        catch (SqlException ex) when (ex.Number is 51120 or 51121 or 51122 or 2601 or 2627 or 547)
        {
            return Results.Problem(statusCode: 409, title: "Machine evidence profile revision conflict");
        }
        catch (SqlException) { return Results.Problem(statusCode: 503, title: "Machine profile writer unavailable"); }
    }
}
