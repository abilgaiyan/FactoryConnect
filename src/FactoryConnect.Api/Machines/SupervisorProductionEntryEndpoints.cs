using FactoryConnect.Api.Commissioning;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Api.Machines;

/// <summary>Local commissioning read of durable supervisor declarations; never publishes OEE.</summary>
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
        return endpoints;
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
}
