using System.Net;
using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class MtConnectProtocolExceptionTests 
{
    [Fact]
    public void ProtocolExceptionBuildsMessageFromMtConnectErrors()
    {
        var result = new MtConnectErrorResult
        {
            InstanceId = 42,
            Errors =
            [
                new MtConnectError
                {
                    Code = "OUT_OF_RANGE",
                    Message = "Sequence outside buffer.",
                },
            ],
        };

        var exception = new MtConnectProtocolException(
            HttpStatusCode.NotFound,
            result);

        Assert.Equal(
            HttpStatusCode.NotFound,
            exception.StatusCode);

        Assert.Same(
            result,
            exception.ErrorResult);

        Assert.Contains(
            "OUT_OF_RANGE",
            exception.Message,
            StringComparison.Ordinal);
    }
}
