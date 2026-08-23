using Xunit;

namespace FactoryConnect.Protocols.MTConnect.Tests;

public sealed class MtConnectErrorParserTests
{
    [Fact]
    public void TryParseParsesOutOfRangeError()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="42" />
              <Errors>
                <Error errorCode="OUT_OF_RANGE">
                  Requested sequence is outside the available range.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        var parsed = MtConnectErrorParser.TryParse(
            xml,
            out var result);

        Assert.True(parsed);
        Assert.NotNull(result);

        Assert.Equal(42UL, result.InstanceId);

        var error = Assert.Single(result.Errors);

        Assert.Equal("OUT_OF_RANGE", error.Code);
        Assert.Equal(
            "Requested sequence is outside the available range.",
            error.Message);
    }

    [Fact]
    public void TryParseParsesNoDeviceError()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="42" />
              <Errors>
                <Error errorCode="NO_DEVICE">
                  Device was not found.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        var parsed = MtConnectErrorParser.TryParse(
            xml,
            out var result);

        Assert.True(parsed);
        Assert.NotNull(result);

        var error = Assert.Single(result.Errors);

        Assert.Equal("NO_DEVICE", error.Code);
        Assert.Equal("Device was not found.", error.Message);
    }

    [Fact]
    public void TryParsePreservesMultipleErrors()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="42" />
              <Errors>
                <Error errorCode="OUT_OF_RANGE">
                  Sequence outside buffer.
                </Error>
                <Error errorCode="INVALID_REQUEST">
                  Request is invalid.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        var parsed = MtConnectErrorParser.TryParse(
            xml,
            out var result);

        Assert.True(parsed);
        Assert.NotNull(result);
        Assert.Equal(2, result.Errors.Count);

        Assert.Equal(
            "OUT_OF_RANGE",
            result.Errors[0].Code);

        Assert.Equal(
            "INVALID_REQUEST",
            result.Errors[1].Code);
    }

    [Fact]
    public void TryParseAllowsMissingInstanceId()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header />
              <Errors>
                <Error errorCode="INVALID_REQUEST">
                  Request is invalid.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        var parsed = MtConnectErrorParser.TryParse(
            xml,
            out var result);

        Assert.True(parsed);
        Assert.NotNull(result);
        Assert.Null(result.InstanceId);
    }

    [Fact]
    public void TryParseReturnsFalseForNonMtConnectErrorDocument()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="42" />
              <Streams />
            </MTConnectStreams>
            """;

        var parsed = MtConnectErrorParser.TryParse(
            xml,
            out var result);

        Assert.False(parsed);
        Assert.Null(result);
    }

    [Fact]
    public void TryParseReturnsFalseForInvalidXml()
    {
        const string xml = """
            <MTConnectError>
              <Header>
            """;

        var parsed = MtConnectErrorParser.TryParse(
            xml,
            out var result);

        Assert.False(parsed);
        Assert.Null(result);
    }

    [Fact]
    public void TryParseReturnsFalseForEmptyContent()
    {
        var parsed = MtConnectErrorParser.TryParse(
            string.Empty,
            out var result);

        Assert.False(parsed);
        Assert.Null(result);
    }

    [Fact]
    public void TryParseRejectsMissingErrorCode()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="42" />
              <Errors>
                <Error>
                  Error without code.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        Assert.Throws<InvalidDataException>(
            () => MtConnectErrorParser.TryParse(
                xml,
                out _));
    }

    [Fact]
    public void TryParseRejectsResponseWithoutErrors()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="42" />
              <Errors />
            </MTConnectError>
            """;

        Assert.Throws<InvalidDataException>(
            () => MtConnectErrorParser.TryParse(
                xml,
                out _));
    }

    [Fact]
    public void TryParseRejectsInvalidInstanceId()
    {
        const string xml = """
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.5">
              <Header instanceId="invalid" />
              <Errors>
                <Error errorCode="OUT_OF_RANGE">
                  Sequence outside buffer.
                </Error>
              </Errors>
            </MTConnectError>
            """;

        Assert.Throws<InvalidDataException>(
            () => MtConnectErrorParser.TryParse(
                xml,
                out _));
    }
}
