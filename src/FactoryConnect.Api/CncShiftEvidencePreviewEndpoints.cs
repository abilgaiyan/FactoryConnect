using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

/// <summary>Explicitly enabled loopback-only diagnostic; not a production quantity authority.</summary>
public static class CncShiftEvidencePreviewEndpoints
{
    public sealed record Request(Guid MachineId, string AssignmentId, string ShiftId,
        DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
        string CounterAddress, string StreamKeyHex);

    public static IEndpointRouteBuilder MapCncShiftEvidencePreviewEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/supervisor/cnc-shift-evidence-preview", async (
            HttpContext context, IConfiguration configuration, Request request,
            CancellationToken cancellationToken) =>
        {
            if (!configuration.GetValue<bool>("CncShiftPreview:Enabled"))
                return Results.NotFound();
            if (context.Connection.RemoteIpAddress is null ||
                !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var connectionString = configuration["CncShiftPreview:ReadOnlyConnectionString"];
            if (string.IsNullOrWhiteSpace(connectionString))
                return Results.Problem("CNC preview read-only database is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            byte[] stream;
            try
            {
                stream = Convert.FromHexString(request.StreamKeyHex);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentNullException)
            {
                return Results.BadRequest("Invalid stream key hexadecimal encoding.");
            }
            try
            {
                var reader = new SqlServerCncShiftEvidencePreviewReader(connectionString);
                var preview = await reader.ReadAsync(request.MachineId,
                    request.AssignmentId, request.ShiftId, request.StartsAtUtc,
                    request.EndsAtUtc, request.CounterAddress, stream, cancellationToken);
                return Results.Ok(new { Status = preview.HasEvidence ? "provisional" : "no-evidence",
                    Authority = "diagnostic-only", Preview = preview });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest("Invalid CNC preview selection.");
            }
            catch (SqlException)
            {
                return Results.Problem("CNC preview evidence is unavailable or ambiguous.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (InvalidDataException)
            {
                return Results.Problem("CNC preview evidence is incomplete.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });
        return endpoints;
    }
}
