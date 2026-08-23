using System.Net;

namespace FactoryConnect.Protocols.MTConnect;

public sealed class MtConnectProtocolException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public MtConnectErrorResult ErrorResult { get; }

    public MtConnectProtocolException(
        HttpStatusCode statusCode,
        MtConnectErrorResult errorResult)
        : base(BuildMessage(statusCode, errorResult))
    {
        StatusCode = statusCode;
        ErrorResult = errorResult;
    }

    private static string BuildMessage(
        HttpStatusCode statusCode,
        MtConnectErrorResult errorResult)
    {
        ArgumentNullException.ThrowIfNull(errorResult);

        var errors = string.Join(
            "; ",
            errorResult.Errors.Select(error =>
                $"{error.Code}: {error.Message}"));

        return
            $"MTConnect protocol error " +
            $"({(int)statusCode} {statusCode}): {errors}";
    }
}
