using System.Security.Claims;
using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api.Machines;

public static class OperationMasterEndpoints
{
    public static IEndpointRouteBuilder MapOperationMasterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/supervisor/v1/operations", ReadActiveAsync)
            .RequireAuthorization("OperationalRead").WithName("ReadLocalActiveOperations")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapGet("/api/admin/v1/operations", ReadAllAsync)
            .RequireAuthorization("AdministratorWrite").WithName("ReadLocalAllOperations")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapPut("/api/admin/v1/operations", SaveAsync)
            .RequireAuthorization("AdministratorWrite").WithName("UpsertLocalOperation")
            .WithGroupName("supervisor-local-v1");
        return endpoints;
    }

    private static Task<IResult> ReadActiveAsync(string partNumber, HttpContext context, IConfiguration configuration,
        CancellationToken cancellationToken, int offset = 0, int limit = 50) =>
        ReadAsync(partNumber, true, offset, limit, context, configuration, cancellationToken);

    private static Task<IResult> ReadAllAsync(string partNumber, HttpContext context, IConfiguration configuration,
        CancellationToken cancellationToken, int offset = 0, int limit = 50) =>
        ReadAsync(partNumber, false, offset, limit, context, configuration, cancellationToken);

    private static async Task<IResult> ReadAsync(string partNumber, bool activeOnly, int offset, int limit,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        if (offset < 0 || limit is < 1 or > 100)
            return Results.BadRequest(new { reason = "InvalidOperationPage" });
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Operation master reader not configured");
        try
        {
            return Results.Ok(await new SqlServerOperationMasterStore(connectionString)
                .ListAsync(partNumber, activeOnly, offset, limit, cancellationToken));
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidOperationPage" });
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Operation master read unavailable");
        }
    }

    private static async Task<IResult> SaveAsync(OperationMasterRecord operation, HttpContext context,
        IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("LocalOperationMaster:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local operation master writes disabled");
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var connectionString = configuration["LocalOperationMaster:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 ||
            string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Operation master writer not configured");
        try
        {
            await new SqlServerOperationMasterStore(connectionString)
                .UpsertAsync(operation, actor, clock.GetUtcNow(), cancellationToken);
            return Results.Ok(operation);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidOperationMasterEntry" });
        }
        catch (SqlException exception) when (exception.Number is 547 or 2601 or 2627)
        {
            return Results.Problem(statusCode: 409, title: "Operation master conflict");
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Operation master write unavailable");
        }
    }
}
