using System.Security.Claims;
using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api.Machines;

public static class PartMasterEndpoints
{
    public static IEndpointRouteBuilder MapPartMasterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/supervisor/v1/parts", ReadActiveAsync)
            .RequireAuthorization("OperationalRead").WithName("ReadLocalActiveParts")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapGet("/api/admin/v1/parts", ReadAllAsync)
            .RequireAuthorization("AdministratorWrite").WithName("ReadLocalAllParts")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapPut("/api/admin/v1/parts", SaveAsync)
            .RequireAuthorization("AdministratorWrite").WithName("UpsertLocalPart")
            .WithGroupName("supervisor-local-v1");
        return endpoints;
    }

    private static Task<IResult> ReadActiveAsync(HttpContext context, IConfiguration configuration,
        CancellationToken cancellationToken, int offset = 0, int limit = 50) =>
        ReadAsync(true, offset, limit, context, configuration, cancellationToken);

    private static Task<IResult> ReadAllAsync(HttpContext context, IConfiguration configuration,
        CancellationToken cancellationToken, int offset = 0, int limit = 50) =>
        ReadAsync(false, offset, limit, context, configuration, cancellationToken);

    private static async Task<IResult> ReadAsync(bool activeOnly, int offset, int limit,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        if (offset < 0 || limit is < 1 or > 100)
            return Results.BadRequest(new { reason = "InvalidPartPage" });
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Part master reader not configured");
        try
        {
            return Results.Ok(await new SqlServerPartMasterStore(connectionString)
                .ListAsync(activeOnly, offset, limit, cancellationToken));
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Part master read unavailable");
        }
    }

    private static async Task<IResult> SaveAsync(PartMasterRecord part, HttpContext context,
        IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("LocalPartMaster:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local part master writes disabled");
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var connectionString = configuration["LocalPartMaster:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 ||
            string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Part master writer not configured");
        try
        {
            await new SqlServerPartMasterStore(connectionString)
                .UpsertAsync(part, actor, clock.GetUtcNow(), cancellationToken);
            return Results.Ok(part);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidPartMasterEntry" });
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            return Results.Problem(statusCode: 409, title: "Part master conflict");
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Part master write unavailable");
        }
    }
}
