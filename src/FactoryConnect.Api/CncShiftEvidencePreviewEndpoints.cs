using FactoryConnect.Persistence.SqlServer;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Machines;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api;

/// <summary>Explicitly enabled loopback-only diagnostic; not a production quantity authority.</summary>
public static partial class CncShiftEvidencePreviewEndpoints
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "CNC preview SQL timeout. Trace {TraceId}")]
    private static partial void LogSqlTimeout(ILogger logger, string traceId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "CNC preview SQL error {Number}. Trace {TraceId}")]
    private static partial void LogSqlError(ILogger logger, int number, string traceId);

    public sealed record Request(Guid MachineId, string AssignmentId, string ShiftId,
        DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
        string? CounterAddress = null, string? StreamKeyHex = null);

    public static IEndpointRouteBuilder MapCncShiftEvidencePreviewEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/supervisor/cnc-shift-evidence-preview", async (
            HttpContext context, IConfiguration configuration, Request request,
            ILogger<SqlServerCncShiftEvidencePreviewReader> logger,
            ICurrentStateOwnerResolver owners,
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
            try
            {
                var reader = new SqlServerCncShiftEvidencePreviewReader(connectionString);
                CncShiftEvidenceClassifier.Preview preview;
                if (request.CounterAddress is null && request.StreamKeyHex is null)
                {
                    if (request.MachineId == Guid.Empty)
                        return Results.BadRequest("Machine required.");
                    var profiles = await new SqlServerMachineProductionEvidenceProfileStore(connectionString)
                        .ListAsync(request.MachineId, cancellationToken);
                    var profile = CounterProfileIntervalGuard.EffectiveUnchanged(
                        profiles, request.StartsAtUtc, request.EndsAtUtc);
                    if (profile is null || string.IsNullOrWhiteSpace(profile.CounterDataItemId) ||
                        profile.CounterSemantics is not ("CumulativeResettable" or "CumulativeMonotonic"))
                        return Results.Problem("No unchanged cumulative counter profile for this shift.",
                            statusCode: 422);
                    if (owners.Resolve(new MachineId(request.MachineId)) is not CurrentStateExactlyOneOwner owner)
                        return Results.Problem("Counter stream owner unavailable.", statusCode: 422);
                    // Current owner is a diagnostic candidate, not proof of historical identity.
                    preview = await reader.ReadForStreamAsync(request.MachineId, request.AssignmentId,
                        request.ShiftId, request.StartsAtUtc, request.EndsAtUtc,
                        profile.CounterDataItemId, owner.Binding.ObservationStreamId, cancellationToken);
                }
                else
                {
                    if (request.CounterAddress is null || request.StreamKeyHex is null)
                        return Results.BadRequest("Supply both diagnostic overrides or neither.");
                    byte[] stream;
                    try { stream = Convert.FromHexString(request.StreamKeyHex); }
                    catch (FormatException) { return Results.BadRequest("Invalid stream key hexadecimal encoding."); }
                    preview = await reader.ReadAsync(request.MachineId, request.AssignmentId,
                        request.ShiftId, request.StartsAtUtc, request.EndsAtUtc,
                        request.CounterAddress, stream, cancellationToken);
                }
                return Results.Ok(new { Status = preview.HasEvidence ? "provisional" : "no-evidence",
                    Authority = "diagnostic-only", Preview = preview });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest("Invalid CNC preview selection.");
            }
            catch (SqlException exception) when (exception.Number == -2)
            {
                LogSqlTimeout(logger, context.TraceIdentifier);
                return Results.Problem("CNC preview database query timed out.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (SqlException exception) when (exception.Number == 51120)
            {
                return Results.Problem("Exact commissioned shift occurrence missing or ambiguous.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (SqlException exception)
            {
                LogSqlError(logger, exception.Number, context.TraceIdentifier);
                return Results.Problem("CNC preview database query failed.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (InvalidDataException)
            {
                return Results.Problem("CNC preview evidence is incomplete.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }).RequireAuthorization("OperationalRead").ExcludeFromDescription();
        return endpoints;
    }
}
