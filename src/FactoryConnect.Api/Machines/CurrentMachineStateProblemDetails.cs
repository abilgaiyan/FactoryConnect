namespace FactoryConnect.Api.Machines;

internal static class CurrentMachineStateProblemDetails
{
    private const string InvalidMachineIdType =
        "urn:factoryconnect:problem:machines:invalid-machine-id";
    private const string AuthorityFailureType =
        "urn:factoryconnect:problem:machines:current-state-authority-failure";

    public static IResult InvalidMachineId() =>
        Problem(
            InvalidMachineIdType,
            "Invalid machine ID",
            "The machineId route value must be a non-empty GUID in D format.",
            StatusCodes.Status400BadRequest,
            "invalid-machine-id");

    public static IResult AuthorityFailure() =>
        Problem(
            AuthorityFailureType,
            "Current machine-state authority unavailable",
            "Authoritative current machine state is temporarily unavailable.",
            StatusCodes.Status503ServiceUnavailable,
            "current-machine-state-authority-failure");

    private static IResult Problem(
        string type,
        string title,
        string detail,
        int statusCode,
        string code) =>
        Results.Problem(
            type: type,
            title: title,
            statusCode: statusCode,
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });
}
