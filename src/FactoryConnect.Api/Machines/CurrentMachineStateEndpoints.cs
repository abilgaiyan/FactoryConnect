using FactoryConnect.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace FactoryConnect.Api.Machines;

public static class CurrentMachineStateEndpoints
{
    public static IEndpointRouteBuilder MapCurrentMachineStateEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
                "/api/machines/v1/{machineId}/current-state",
                ReadCurrentMachineStateAsync)
            .WithTags("Machines")
            .WithName("GetCurrentMachineState")
            .Produces<CurrentMachineStateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    internal static async Task<IResult> ReadCurrentMachineStateAsync(
        string machineId,
        [FromServices] ICurrentMachineStateReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!Guid.TryParseExact(machineId, "D", out var value)
            || value == Guid.Empty
            || !string.Equals(
                machineId,
                value.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
        {
            return CurrentMachineStateProblemDetails.InvalidMachineId();
        }

        var result = await reader
            .ReadAsync(new MachineId(value), cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            CurrentMachineStateEvidence or CurrentMachineStateNoEvidence =>
                Results.Ok(CurrentMachineStateHttpMapper.ToResponse(result)),
            CurrentMachineStateAuthorityFailure =>
                CurrentMachineStateProblemDetails.AuthorityFailure(),
            _ => throw new InvalidOperationException(
                "The current-machine-state reader returned an unsupported result."),
        };
    }
}
