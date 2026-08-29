using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricReportingErrorEndpointTests
{
    private const string ShiftRoute = "/api/reporting/v1/operational-metrics/shifts/query";
    private const string ProductionDayRoute = "/api/reporting/v1/operational-metrics/production-days/query";
    private const string InvalidRequestType = "urn:factoryconnect:problem:reporting:invalid-request";
    private const string MalformedTokenType = "urn:factoryconnect:problem:reporting:malformed-continuation-token";
    private const string IncompatibleTokenType = "urn:factoryconnect:problem:reporting:incompatible-continuation-token";

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task PageSizeOutsideContractReturnsStableProblemDetails(int pageSize)
    {
        var reader = new InvocationTrackingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            ShiftRoute,
            CreateShiftRequest(pageSize: pageSize));

        await AssertProblemAsync(response, InvalidRequestType, "invalid-reporting-query");
        Assert.Equal(0, reader.InvocationCount);
    }

    [Fact]
    public async Task EmptySourceSelectionReturnsValidationProblemRatherThanEmptySuccess()
    {
        var reader = new InvocationTrackingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            ShiftRoute,
            CreateShiftRequest(sources: []));

        await AssertProblemAsync(response, InvalidRequestType, "invalid-reporting-query");
        Assert.Equal(0, reader.InvocationCount);
    }

    [Fact]
    public async Task DuplicateMachineBindingReturnsValidationProblem()
    {
        var machineId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var reader = new InvocationTrackingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            ShiftRoute,
            CreateShiftRequest(sources:
            [
                new ReportingSourceRequest(machineId, "projection-a"),
                new ReportingSourceRequest(machineId, "projection-b"),
            ]));

        await AssertProblemAsync(response, InvalidRequestType, "invalid-reporting-query");
        Assert.Equal(0, reader.InvocationCount);
    }

    [Fact]
    public async Task ProcessorBoundToMultipleMachinesReturnsValidationProblem()
    {
        var reader = new InvocationTrackingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            ShiftRoute,
            CreateShiftRequest(sources:
            [
                new ReportingSourceRequest(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "projection-a"),
                new ReportingSourceRequest(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "projection-a"),
            ]));

        await AssertProblemAsync(response, InvalidRequestType, "invalid-reporting-query");
        Assert.Equal(0, reader.InvocationCount);
    }

    [Fact]
    public async Task DuplicateMetricStatusAndUnsupportedOrderAreValidationErrors()
    {
        var reader = new InvocationTrackingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        var duplicateMetrics = CreateShiftRequest(metrics:
        [
            new OperationalMetricDefinitionRequest("OEE", "1.0"),
            new OperationalMetricDefinitionRequest("OEE", "1.0"),
        ]);
        using var metricResponse = await client.PostAsJsonAsync(ShiftRoute, duplicateMetrics);
        await AssertProblemAsync(metricResponse, InvalidRequestType, "invalid-reporting-query");

        var duplicateStatuses = CreateShiftRequest(statuses: ["calculated", "calculated"]);
        using var statusResponse = await client.PostAsJsonAsync(ShiftRoute, duplicateStatuses);
        await AssertProblemAsync(statusResponse, InvalidRequestType, "invalid-reporting-query");

        var unsupportedOrder = CreateShiftRequest(order: "machine-ascending");
        using var orderResponse = await client.PostAsJsonAsync(ShiftRoute, unsupportedOrder);
        await AssertProblemAsync(orderResponse, InvalidRequestType, "invalid-reporting-query");

        Assert.Equal(0, reader.InvocationCount);
    }

    [Fact]
    public async Task UnsupportedStatusAndContradictoryShiftRangeReturnValidationProblem()
    {
        var reader = new InvocationTrackingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var statusResponse = await client.PostAsJsonAsync(
            ShiftRoute,
            CreateShiftRequest(statuses: ["pending"]));
        await AssertProblemAsync(statusResponse, InvalidRequestType, "invalid-reporting-query");

        var startsAt = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        using var rangeResponse = await client.PostAsJsonAsync(
            ShiftRoute,
            CreateShiftRequest(startsAt: startsAt, startsBefore: startsAt));
        await AssertProblemAsync(rangeResponse, InvalidRequestType, "invalid-reporting-query");

        Assert.Equal(0, reader.InvocationCount);
    }

    [Fact]
    public async Task NonUtcShiftRangeAndContradictoryProductionDayRangeReturnValidationProblem()
    {
        var reader = new InvocationTrackingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        var localOffset = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.FromHours(5.5));
        using var shiftResponse = await client.PostAsJsonAsync(
            ShiftRoute,
            CreateShiftRequest(
                startsAt: localOffset,
                startsBefore: localOffset.AddHours(8)));
        await AssertProblemAsync(shiftResponse, InvalidRequestType, "invalid-reporting-query");

        var day = new DateOnly(2026, 8, 29);
        using var productionDayResponse = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(from: day, to: day));
        await AssertProblemAsync(productionDayResponse, InvalidRequestType, "invalid-reporting-query");

        Assert.Equal(0, reader.InvocationCount);
    }

    [Fact]
    public async Task MalformedContinuationTokenReturnsDedicatedProblemDetails()
    {
        var provider = new MutableReportingProvider([]);
        await using var app = await CreateAppAsync(CreateRealQueryReader(provider));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(continuationToken: "not-a-valid-base64url-token!"));

        await AssertProblemAsync(
            response,
            MalformedTokenType,
            "malformed-continuation-token");
        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task ContinuationTokenFromDifferentQueryReturnsDedicatedProblemDetails()
    {
        var source = CreateSource();
        var provider = new MutableReportingProvider(
        [
            CreateProductionDaySummary(source, new DateOnly(2026, 8, 1), "Availability"),
            CreateProductionDaySummary(source, new DateOnly(2026, 8, 2), "Availability"),
        ]);
        await using var app = await CreateAppAsync(CreateRealQueryReader(provider));
        using var client = app.GetTestClient();

        using var firstResponse = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(pageSize: 1));
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.NotNull(firstPage?.ContinuationToken);

        using var incompatibleResponse = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(
                order: "period-descending",
                pageSize: 1,
                continuationToken: firstPage.ContinuationToken));

        await AssertProblemAsync(
            incompatibleResponse,
            IncompatibleTokenType,
            "incompatible-continuation-token");
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task ContinuationTokenRemainsValidWhenPageSizeChanges()
    {
        var source = CreateSource();
        var provider = new MutableReportingProvider(
        [
            CreateProductionDaySummary(source, new DateOnly(2026, 8, 1), "Availability"),
            CreateProductionDaySummary(source, new DateOnly(2026, 8, 2), "Performance"),
            CreateProductionDaySummary(source, new DateOnly(2026, 8, 3), "Quality"),
        ]);
        await using var app = await CreateAppAsync(CreateRealQueryReader(provider));
        using var client = app.GetTestClient();

        using var firstResponse = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(pageSize: 1));
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.NotNull(firstPage);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.ContinuationToken);

        using var secondResponse = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(
                pageSize: 2,
                continuationToken: firstPage.ContinuationToken));
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.NotNull(secondPage);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal(new DateOnly(2026, 8, 2), secondPage.Items[0].ProductionDay?.BusinessDate);
        Assert.Equal(new DateOnly(2026, 8, 3), secondPage.Items[1].ProductionDay?.BusinessDate);
        Assert.Null(secondPage.ContinuationToken);
    }

    [Fact]
    public async Task MissingPriorCursorRowDoesNotMakeTokenStale()
    {
        var source = CreateSource();
        var first = CreateProductionDaySummary(source, new DateOnly(2026, 8, 1), "Availability");
        var second = CreateProductionDaySummary(source, new DateOnly(2026, 8, 2), "Performance");
        var third = CreateProductionDaySummary(source, new DateOnly(2026, 8, 3), "Quality");
        var provider = new MutableReportingProvider([first, second, third]);
        await using var app = await CreateAppAsync(CreateRealQueryReader(provider));
        using var client = app.GetTestClient();

        using var firstResponse = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(pageSize: 1));
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();
        Assert.NotNull(firstPage?.ContinuationToken);

        provider.Remove(first.Key);

        using var secondResponse = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest(
                pageSize: 2,
                continuationToken: firstPage.ContinuationToken));
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.NotNull(secondPage);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal(new DateOnly(2026, 8, 2), secondPage.Items[0].ProductionDay?.BusinessDate);
        Assert.Equal(new DateOnly(2026, 8, 3), secondPage.Items[1].ProductionDay?.BusinessDate);
    }

    [Fact]
    public async Task ValidQueryWithNoMatchesReturnsEmptySuccessfulPage()
    {
        var provider = new MutableReportingProvider([]);
        await using var app = await CreateAppAsync(CreateRealQueryReader(provider));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            ProductionDayRoute,
            CreateProductionDayRequest());
        var page = await response.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Null(page.ContinuationToken);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task GeneratedOpenApiDocumentsProblemDetailsForBothReportingOperations()
    {
        await using var app = await CreateAppAsync(new InvocationTrackingReader());
        using var client = app.GetTestClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");

        AssertProblemResponseDocumented(paths, ShiftRoute);
        AssertProblemResponseDocumented(paths, ProductionDayRoute);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        string expectedType,
        string expectedCode)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(expectedType, root.GetProperty("type").GetString());
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("exception", out _));
        Assert.False(root.TryGetProperty("stackTrace", out _));
    }

    private static void AssertProblemResponseDocumented(JsonElement paths, string path)
    {
        var operation = paths.GetProperty(path).GetProperty("post");
        var badRequest = operation.GetProperty("responses").GetProperty("400");
        Assert.True(
            badRequest.GetProperty("content").TryGetProperty(
                "application/problem+json",
                out _));
    }

    private static ShiftOperationalMetricQueryRequest CreateShiftRequest(
        IReadOnlyList<ReportingSourceRequest>? sources = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? startsBefore = null,
        IReadOnlyList<OperationalMetricDefinitionRequest>? metrics = null,
        IReadOnlyList<string>? statuses = null,
        string order = "period-ascending",
        int pageSize = 25,
        string? continuationToken = null)
    {
        var start = startsAt ?? new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        return new ShiftOperationalMetricQueryRequest(
            sources ?? [CreateSource()],
            start,
            startsBefore ?? start.AddDays(1),
            metrics,
            null,
            statuses,
            order,
            pageSize,
            continuationToken);
    }

    private static ProductionDayOperationalMetricQueryRequest CreateProductionDayRequest(
        DateOnly? from = null,
        DateOnly? to = null,
        string order = "period-ascending",
        int pageSize = 25,
        string? continuationToken = null) =>
        new(
            [CreateSource()],
            from ?? new DateOnly(2026, 8, 1),
            to ?? new DateOnly(2026, 9, 1),
            null,
            null,
            null,
            order,
            pageSize,
            continuationToken);

    private static ReportingSourceRequest CreateSource() =>
        new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "projection-reporting");

    private static OperationalMetricProjectionSummary CreateProductionDaySummary(
        ReportingSourceRequest source,
        DateOnly businessDate,
        string metricKey)
    {
        var machineId = new MachineId(source.MachineId);
        return new OperationalMetricProjectionSummary(
            new OperationalMetricProjectionProcessorId(source.ProcessorId),
            new OperationalMetricEvaluationKey(
                machineId,
                new OperationalMetricPeriodId.ProductionDay(
                    new ProductionDayId(new SiteId("site-a"), businessDate)),
                new OperationalMetricDefinitionId(metricKey, "1.0"),
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            new MetricAggregationCheckpoint(
                new MetricAggregationProcessorId("aggregation"),
                new MetricInputStreamId(machineId, "metric-inputs"),
                new MetricInputPosition((ulong)businessDate.Day)));
    }

    private static IOperationalMetricQueryReader CreateRealQueryReader(
        IOperationalMetricReportingQueryProvider provider) =>
        new OperationalMetricQueryReader(
            new OperationalMetricReportingQueryReader(provider));

    private static async Task<WebApplication> CreateAppAsync(IOperationalMetricQueryReader reader)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(reader);
        builder.Services.AddOpenApi();

        var app = builder.Build();
        app.MapOpenApi();
        app.MapOperationalMetricReportingEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class InvocationTrackingReader : IOperationalMetricQueryReader
    {
        public int InvocationCount { get; private set; }

        public ValueTask<ReportingPage<OperationalMetricQueryItem>> ReadAsync(
            OperationalMetricReportQuery query,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(
                new ReportingPage<OperationalMetricQueryItem>([], null));
        }
    }

    private sealed class MutableReportingProvider(
        IEnumerable<OperationalMetricProjectionSummary> summaries)
        : IOperationalMetricReportingQueryProvider
    {
        private readonly List<OperationalMetricProjectionSummary> _summaries = [.. summaries];

        public int InvocationCount { get; private set; }

        public void Remove(OperationalMetricEvaluationKey key) =>
            _summaries.RemoveAll(summary => summary.Key == key);

        public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadWindowAsync(
            OperationalMetricReportQuery query,
            OperationalMetricEvaluationKey? startAfter,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;

            var comparer = OperationalMetricReportOrdering.GetEvaluationKeyComparer(query.Order);
            var items = _summaries
                .Where(summary => OperationalMetricReportQuerySemantics.Matches(query, summary))
                .OrderBy(summary => summary.Key, comparer)
                .Where(summary => startAfter is null || comparer.Compare(startAfter, summary.Key) < 0)
                .Take(maximumCount)
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<OperationalMetricProjectionSummary>>(items);
        }
    }
}
