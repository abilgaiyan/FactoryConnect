using System.Net;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Machines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class CurrentMachineStateEndpointTests
{
    private const string RoutePrefix = "/api/machines/v1/";
    private static readonly Guid MachineGuid =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly MachineId Machine = new(MachineGuid);
    private static readonly DateTimeOffset ReadAsOf =
        new(2026, 9, 21, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Current, CurrentStateUsability.Current, "complete", "current", "current")]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Stale, CurrentStateUsability.Stale, "complete", "stale", "stale")]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Indeterminate, CurrentStateUsability.Indeterminate, "complete", "indeterminate", "indeterminate")]
    [InlineData(CurrentStateCoverage.Behind, CurrentStateFreshness.Current, CurrentStateUsability.Behind, "behind", "current", "behind")]
    [InlineData(CurrentStateCoverage.Indeterminate, CurrentStateFreshness.Current, CurrentStateUsability.Indeterminate, "indeterminate", "current", "indeterminate")]
    public async Task EvidenceSerializesFrozenCoverageFreshnessAndUsability(
        CurrentStateCoverage coverage,
        CurrentStateFreshness freshness,
        CurrentStateUsability usability,
        string expectedCoverage,
        string expectedFreshness,
        string expectedUsability)
    {
        var reader = new StubReader((machineId, _) =>
            Task.FromResult<CurrentMachineStateReadResult>(
                new CurrentMachineStateEvidence(
                    machineId,
                    MachineState.Running,
                    coverage,
                    freshness,
                    usability,
                    ReadAsOf)));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Route(MachineGuid));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(MachineGuid.ToString("D"), root.GetProperty("machineId").GetString());
        Assert.Equal("evidence", root.GetProperty("outcome").GetString());
        Assert.Equal(expectedCoverage, root.GetProperty("coverage").GetString());
        var evidence = root.GetProperty("evidence");
        Assert.Equal("running", evidence.GetProperty("machineState").GetString());
        Assert.Equal(expectedFreshness, evidence.GetProperty("freshness").GetString());
        Assert.Equal(expectedUsability, evidence.GetProperty("usability").GetString());
        Assert.Equal(ReadAsOf, evidence.GetProperty("readAsOf").GetDateTimeOffset());
        AssertNoInternalFields(root);
    }

    [Theory]
    [InlineData(MachineState.Unknown, "unknown")]
    [InlineData(MachineState.Stopped, "stopped")]
    [InlineData(MachineState.Idle, "idle")]
    [InlineData(MachineState.Running, "running")]
    [InlineData(MachineState.Fault, "fault")]
    public async Task EvidenceSerializesFrozenMachineStateVocabulary(
        MachineState machineState,
        string expected)
    {
        var reader = new StubReader((machineId, _) =>
            Task.FromResult<CurrentMachineStateReadResult>(
                new CurrentMachineStateEvidence(
                    machineId,
                    machineState,
                    CurrentStateCoverage.Complete,
                    CurrentStateFreshness.Current,
                    CurrentStateUsability.Current,
                    ReadAsOf)));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Route(MachineGuid));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            expected,
            document.RootElement.GetProperty("evidence").GetProperty("machineState").GetString());
    }

    [Theory]
    [InlineData(CurrentStateCoverage.Complete, "complete")]
    [InlineData(CurrentStateCoverage.Behind, "behind")]
    [InlineData(CurrentStateCoverage.Indeterminate, "indeterminate")]
    public async Task NoEvidencePreservesCoverageAndExplicitNullEvidence(
        CurrentStateCoverage coverage,
        string expectedCoverage)
    {
        var reader = new StubReader((machineId, _) =>
            Task.FromResult<CurrentMachineStateReadResult>(
                new CurrentMachineStateNoEvidence(machineId, coverage)));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Route(MachineGuid));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("no-evidence", root.GetProperty("outcome").GetString());
        Assert.Equal(expectedCoverage, root.GetProperty("coverage").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("evidence").ValueKind);
        AssertNoInternalFields(root);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111 ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidMachineIdReturnsExactProblemWithoutCallingReader(string machineId)
    {
        var reader = new StubReader((_, _) =>
            throw new InvalidOperationException("Reader must not be called."));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(RoutePrefix + machineId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertProblemAsync(
            response,
            400,
            "urn:factoryconnect:problem:machines:invalid-machine-id",
            "Invalid machine ID",
            "The machineId route value must be a non-empty GUID in D format.",
            "invalid-machine-id");
        Assert.Equal(0, reader.InvocationCount);
    }

    [Theory]
    [InlineData(CurrentStateAuthorityFailureReason.NoOwner)]
    [InlineData(CurrentStateAuthorityFailureReason.AmbiguousOwner)]
    [InlineData(CurrentStateAuthorityFailureReason.MissingPolicyAuthority)]
    [InlineData(CurrentStateAuthorityFailureReason.AmbiguousPolicyAuthority)]
    [InlineData(CurrentStateAuthorityFailureReason.UnsupportedPolicyAuthority)]
    [InlineData(CurrentStateAuthorityFailureReason.StableCutUnavailable)]
    [InlineData(CurrentStateAuthorityFailureReason.ContradictoryAttribution)]
    [InlineData(CurrentStateAuthorityFailureReason.ContinuityPolicyMismatch)]
    [InlineData(CurrentStateAuthorityFailureReason.UnauthorizedStateAuthority)]
    [InlineData(CurrentStateAuthorityFailureReason.InconsistentAuthority)]
    public async Task EveryAuthorityFailureReasonReturnsSameExactServiceUnavailableProblem(
        CurrentStateAuthorityFailureReason reason)
    {
        var reader = new StubReader((machineId, _) =>
            Task.FromResult<CurrentMachineStateReadResult>(
                new CurrentMachineStateAuthorityFailure(machineId, reason)));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Route(MachineGuid));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertProblemAsync(
            response,
            503,
            "urn:factoryconnect:problem:machines:current-state-authority-failure",
            "Current machine-state authority unavailable",
            "Authoritative current machine state is temporarily unavailable.",
            "current-machine-state-authority-failure");
    }

    [Fact]
    public async Task RequestCancellationPropagatesIntoCurrentStateReader()
    {
        var reader = new CancellationObservingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetAsync(Route(MachineGuid), cancellation.Token);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await reader.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnknownReaderExceptionPropagates()
    {
        var reader = new StubReader((_, _) =>
            throw new CurrentStateTestException());
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        await Assert.ThrowsAsync<CurrentStateTestException>(
            () => client.GetAsync(Route(MachineGuid)));
    }

    [Fact]
    public async Task ValidUnknownMachineIdentityIsNotConvertedToNotFound()
    {
        var reader = new StubReader((machineId, _) =>
            Task.FromResult<CurrentMachineStateReadResult>(
                new CurrentMachineStateNoEvidence(
                    machineId,
                    CurrentStateCoverage.Indeterminate)));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            Route(Guid.Parse("99999999-9999-9999-9999-999999999999")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GeneratedOpenApiDocumentMatchesFrozenCurrentStateContract()
    {
        var reader = new StubReader((machineId, _) =>
            Task.FromResult<CurrentMachineStateReadResult>(
                new CurrentMachineStateNoEvidence(
                    machineId,
                    CurrentStateCoverage.Complete)));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var operation = root
            .GetProperty("paths")
            .GetProperty("/api/machines/v1/{machineId}/current-state")
            .GetProperty("get");
        Assert.Equal("GetCurrentMachineState", operation.GetProperty("operationId").GetString());

        var responses = operation.GetProperty("responses");
        AssertResponseMediaType(responses, "200", "application/json");
        AssertResponseMediaType(responses, "400", "application/problem+json");
        AssertResponseMediaType(responses, "503", "application/problem+json");
        Assert.False(responses.TryGetProperty("404", out _));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var responseSchema = schemas.GetProperty(nameof(CurrentMachineStateResponse));
        AssertRequired(
            responseSchema,
            "machineId",
            "outcome",
            "coverage",
            "evidence");
        AssertPropertyEnum(
            responseSchema,
            "outcome",
            "evidence",
            "no-evidence");
        AssertPropertyEnum(
            responseSchema,
            "coverage",
            "complete",
            "behind",
            "indeterminate");
        Assert.True(
            IsNullable(responseSchema.GetProperty("properties").GetProperty("evidence")));

        var evidenceSchema = schemas.GetProperty(nameof(CurrentMachineStateEvidenceResponse));
        AssertRequired(
            evidenceSchema,
            "machineState",
            "freshness",
            "usability",
            "readAsOf");
        AssertPropertyEnum(
            evidenceSchema,
            "machineState",
            "unknown",
            "stopped",
            "idle",
            "running",
            "fault");
        AssertPropertyEnum(
            evidenceSchema,
            "freshness",
            "current",
            "stale",
            "indeterminate");
        AssertPropertyEnum(
            evidenceSchema,
            "usability",
            "current",
            "stale",
            "indeterminate",
            "behind");

        var serializedOpenApi = root.GetRawText();
        foreach (var forbidden in ForbiddenInternalFields)
        {
            Assert.DoesNotContain(
                "\"" + forbidden + "\"",
                serializedOpenApi,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly string[] ForbiddenInternalFields =
    [
        "processorId",
        "streamId",
        "provider",
        "revision",
        "connectionString",
        "continuityPolicy",
        "authorityCut",
        "reason",
    ];

    private static string Route(Guid machineId) =>
        RoutePrefix + machineId.ToString("D") + "/current-state";

    private static async Task<WebApplication> CreateAppAsync(
        ICurrentMachineStateReader reader)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(reader);
        builder.Services.AddOpenApi(options =>
            options.AddSchemaTransformer<CurrentMachineStateOpenApiTransformer>());

        var app = builder.Build();
        app.MapOpenApi();
        app.MapCurrentMachineStateEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        int expectedStatus,
        string expectedType,
        string expectedTitle,
        string expectedDetail,
        string expectedCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(expectedType, root.GetProperty("type").GetString());
        Assert.Equal(expectedTitle, root.GetProperty("title").GetString());
        Assert.Equal(expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedDetail, root.GetProperty("detail").GetString());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("instance", out _));
        Assert.Equal(
            ["type", "title", "status", "detail", "code"],
            root.EnumerateObject().Select(static property => property.Name).Order().OrderBy(static value => value, StringComparer.Ordinal).ToArray());
    }

    private static void AssertNoInternalFields(JsonElement root)
    {
        var serialized = root.GetRawText();
        foreach (var forbidden in ForbiddenInternalFields)
        {
            Assert.DoesNotContain(
                "\"" + forbidden + "\"",
                serialized,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertResponseMediaType(
        JsonElement responses,
        string statusCode,
        string mediaType)
    {
        var response = responses.GetProperty(statusCode);
        Assert.True(response.GetProperty("content").TryGetProperty(mediaType, out _));
    }

    private static void AssertRequired(
        JsonElement schema,
        params string[] expected)
    {
        var actual = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Where(static value => value is not null)
            .Cast<string>()
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.OrderBy(static value => value, StringComparer.Ordinal), actual);
    }

    private static void AssertPropertyEnum(
        JsonElement schema,
        string propertyName,
        params string[] expected)
    {
        var actual = schema
            .GetProperty("properties")
            .GetProperty(propertyName)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static bool IsNullable(JsonElement schema)
    {
        if (schema.TryGetProperty("nullable", out var nullable)
            && nullable.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(
                static value => value.ValueKind == JsonValueKind.String
                    && value.GetString() == "null");
        }

        return schema.TryGetProperty("anyOf", out var anyOf)
            && anyOf.EnumerateArray().Any(static candidate =>
                candidate.TryGetProperty("type", out var candidateType)
                && candidateType.ValueKind == JsonValueKind.String
                && candidateType.GetString() == "null");
    }

    private sealed class StubReader(
        Func<MachineId, CancellationToken, Task<CurrentMachineStateReadResult>> read)
        : ICurrentMachineStateReader
    {
        public int InvocationCount { get; private set; }

        public Task<CurrentMachineStateReadResult> ReadAsync(
            MachineId machineId,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return read(machineId, cancellationToken);
        }
    }

    private sealed class CancellationObservingReader : ICurrentMachineStateReader
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CurrentMachineStateReadResult> ReadAsync(
            MachineId machineId,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class CurrentStateTestException : Exception;
}
