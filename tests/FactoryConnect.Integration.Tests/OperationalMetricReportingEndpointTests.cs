using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricReportingEndpointTests
{
    [Fact]
    public async Task ShiftEndpointBindsFullQueryAndSerializesCalculatedResponse()
    {
        var machineId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondMachineId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var startsAt = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var endsAt = startsAt.AddHours(8);
        OperationalMetricReportQuery? captured = null;

        var reader = new StubQueryReader((query, _) =>
        {
            captured = query;
            var summary = CreateSummary(
                new MachineId(machineId),
                new OperationalMetricProjectionProcessorId("projection-a"),
                new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(
                    new SiteId("site-a"),
                    new ShiftScheduleAssignmentId("schedule-a"),
                    new ShiftId("shift-a"),
                    startsAt,
                    endsAt)),
                new OperationalMetricDefinitionId("OEE", "1.0"),
                new OperationalMetricEvaluationContextKey
                {
                    ProductionOrderId = new ProductionOrderId("order-1"),
                    OperationId = new OperationId("operation-1"),
                    PartId = new PartId("part-1"),
                    OperatorId = new OperatorId("operator-1"),
                },
                OperationalMetricEvaluationStatus.Calculated,
                0.875m,
                OperationalMetricUnits.Ratio,
                null,
                null,
                42);

            return new ReportingPage<OperationalMetricQueryItem>(
                [OperationalMetricQueryItem.FromSummary(summary)],
                new ReportingContinuationToken("opaque-next-token"));
        });

        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        var request = new ShiftOperationalMetricQueryRequest(
            [
                new ReportingSourceRequest(machineId, "projection-a"),
                new ReportingSourceRequest(secondMachineId, "projection-b"),
            ],
            startsAt,
            startsAt.AddDays(1),
            [
                new OperationalMetricDefinitionRequest("OEE", "1.0"),
                new OperationalMetricDefinitionRequest("Availability", "2.0"),
            ],
            new OperationalMetricContextRequest(
                "order-1",
                "operation-1",
                "part-1",
                "operator-1"),
            ["calculated", "insufficient-evidence"],
            "period-descending",
            37,
            "opaque-input-token");

        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/shifts/query",
            request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var shiftQuery = Assert.IsType<ShiftOperationalMetricReportQuery>(captured);
        Assert.Equal(startsAt, shiftQuery.StartsAtOrAfterUtc);
        Assert.Equal(startsAt.AddDays(1), shiftQuery.StartsBeforeUtc);
        Assert.Equal(OperationalMetricReportOrder.PeriodDescending, shiftQuery.Order);
        Assert.Equal(37, shiftQuery.Page.PageSize);
        Assert.Equal("opaque-input-token", shiftQuery.Page.ContinuationToken?.Value);
        Assert.Collection(
            shiftQuery.Sources.Sources,
            source =>
            {
                Assert.Equal(machineId, source.MachineId.Value);
                Assert.Equal("projection-a", source.ProcessorId.Value);
            },
            source =>
            {
                Assert.Equal(secondMachineId, source.MachineId.Value);
                Assert.Equal("projection-b", source.ProcessorId.Value);
            });
        Assert.Collection(
            shiftQuery.Metrics!.DefinitionIds,
            definition =>
            {
                Assert.Equal("OEE", definition.MetricKey);
                Assert.Equal("1.0", definition.Version);
            },
            definition =>
            {
                Assert.Equal("Availability", definition.MetricKey);
                Assert.Equal("2.0", definition.Version);
            });
        Assert.Equal("order-1", shiftQuery.Context!.ProductionOrderId?.Value);
        Assert.Equal("operation-1", shiftQuery.Context.OperationId?.Value);
        Assert.Equal("part-1", shiftQuery.Context.PartId?.Value);
        Assert.Equal("operator-1", shiftQuery.Context.OperatorId?.Value);
        Assert.Equal(
            [OperationalMetricEvaluationStatus.Calculated, OperationalMetricEvaluationStatus.InsufficientEvidence],
            shiftQuery.Statuses!.Statuses);

        var body = await response.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();
        Assert.NotNull(body);
        Assert.Equal("opaque-next-token", body.ContinuationToken);
        var item = Assert.Single(body.Items);
        Assert.Equal("shift", item.Scope);
        Assert.Equal("projection-a", item.ProcessorId);
        Assert.Equal(machineId, item.MachineId);
        Assert.NotNull(item.Shift);
        Assert.Null(item.ProductionDay);
        Assert.Equal("site-a", item.Shift.SiteId);
        Assert.Equal("schedule-a", item.Shift.ShiftScheduleAssignmentId);
        Assert.Equal("shift-a", item.Shift.ShiftId);
        Assert.Equal(startsAt, item.Shift.StartsAtUtc);
        Assert.Equal(endsAt, item.Shift.EndsAtUtc);
        Assert.Equal("OEE", item.MetricKey);
        Assert.Equal("1.0", item.DefinitionVersion);
        Assert.Equal("calculated", item.Status);
        Assert.Equal(0.875m, item.Value);
        Assert.Null(item.ReasonCode);
        Assert.Equal("aggregation", item.SourceRevision.ProcessorId);
        Assert.Equal(machineId, item.SourceRevision.MachineId);
        Assert.Equal("metric-inputs", item.SourceRevision.StreamKey);
        Assert.Equal(42UL, item.SourceRevision.Position);
    }

    [Fact]
    public async Task ProductionDayEndpointBindsTypedRangeAndSerializesUnavailableResponse()
    {
        var machineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var from = new DateOnly(2026, 8, 1);
        var to = new DateOnly(2026, 9, 1);
        OperationalMetricReportQuery? captured = null;

        var reader = new StubQueryReader((query, _) =>
        {
            captured = query;
            var summary = CreateSummary(
                new MachineId(machineId),
                new OperationalMetricProjectionProcessorId("projection-day"),
                new OperationalMetricPeriodId.ProductionDay(
                    new ProductionDayId(new SiteId("site-b"), new DateOnly(2026, 8, 29))),
                new OperationalMetricDefinitionId("Quality", "1.0"),
                OperationalMetricEvaluationContextKey.Unpartitioned,
                OperationalMetricEvaluationStatus.Unavailable,
                null,
                OperationalMetricUnits.Ratio,
                OperationalMetricEvaluationReasonCode.MissingOperand,
                "GoodCount",
                81);

            return new ReportingPage<OperationalMetricQueryItem>(
                [OperationalMetricQueryItem.FromSummary(summary)],
                null);
        });

        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        var request = new ProductionDayOperationalMetricQueryRequest(
            [new ReportingSourceRequest(machineId, "projection-day")],
            from,
            to,
            null,
            null,
            ["unavailable"],
            "period-ascending",
            25,
            null);

        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/production-days/query",
            request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dayQuery = Assert.IsType<ProductionDayOperationalMetricReportQuery>(captured);
        Assert.Equal(from, dayQuery.FromInclusive);
        Assert.Equal(to, dayQuery.ToExclusive);
        Assert.Equal(OperationalMetricReportOrder.PeriodAscending, dayQuery.Order);
        Assert.Equal(25, dayQuery.Page.PageSize);
        Assert.Null(dayQuery.Page.ContinuationToken);

        var body = await response.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();
        Assert.NotNull(body);
        Assert.Null(body.ContinuationToken);
        var item = Assert.Single(body.Items);
        Assert.Equal("production-day", item.Scope);
        Assert.Null(item.Shift);
        Assert.NotNull(item.ProductionDay);
        Assert.Equal("site-b", item.ProductionDay.SiteId);
        Assert.Equal(new DateOnly(2026, 8, 29), item.ProductionDay.BusinessDate);
        Assert.Equal("unavailable", item.Status);
        Assert.Null(item.Value);
        Assert.Equal(nameof(OperationalMetricEvaluationReasonCode.MissingOperand), item.ReasonCode);
        Assert.Equal("GoodCount", item.ReasonOperandName);
        Assert.Equal(81UL, item.SourceRevision.Position);
    }

    [Fact]
    public async Task EndpointSerializesInsufficientEvidenceWithoutInventingValue()
    {
        var machine = new MachineId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var startsAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var summary = CreateSummary(
            machine,
            new OperationalMetricProjectionProcessorId("projection-insufficient"),
            new OperationalMetricPeriodId.Shift(new ShiftOccurrenceId(
                new SiteId("site-c"),
                new ShiftScheduleAssignmentId("schedule-c"),
                new ShiftId("shift-b"),
                startsAt,
                startsAt.AddHours(8))),
            new OperationalMetricDefinitionId("Performance", "1.0"),
            OperationalMetricEvaluationContextKey.Unpartitioned,
            OperationalMetricEvaluationStatus.InsufficientEvidence,
            null,
            OperationalMetricUnits.Ratio,
            OperationalMetricEvaluationReasonCode.MissingReferenceTime,
            "ProductionReferenceTime",
            99);
        var reader = new StubQueryReader((_, _) => new ReportingPage<OperationalMetricQueryItem>(
            [OperationalMetricQueryItem.FromSummary(summary)],
            null));

        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        var request = new ShiftOperationalMetricQueryRequest(
            [new ReportingSourceRequest(machine.Value, "projection-insufficient")],
            startsAt,
            startsAt.AddDays(1),
            null,
            null,
            ["insufficient-evidence"],
            "period-ascending",
            10,
            null);

        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/shifts/query",
            request);
        var body = await response.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        var item = Assert.Single(body.Items);
        Assert.Equal("insufficient-evidence", item.Status);
        Assert.Null(item.Value);
        Assert.Equal(nameof(OperationalMetricEvaluationReasonCode.MissingReferenceTime), item.ReasonCode);
        Assert.Equal("ProductionReferenceTime", item.ReasonOperandName);
    }

    [Fact]
    public async Task RequestCancellationPropagatesIntoQueryReader()
    {
        var reader = new CancellationObservingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();
        using var cancellation = new CancellationTokenSource();

        var request = new ShiftOperationalMetricQueryRequest(
            [new ReportingSourceRequest(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                "projection-cancel")],
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
            null,
            null,
            null,
            "period-ascending",
            10,
            null);

        var pending = client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/shifts/query",
            request,
            cancellation.Token);
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await reader.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EndpointMetadataLocksVersionedRoutesNamesTagsAndDocumentedResponses()
    {
        var reader = new StubQueryReader((_, _) => new ReportingPage<OperationalMetricQueryItem>([], null));
        await using var app = await CreateAppAsync(reader);

        var endpoints = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("operational-metrics", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(3, endpoints.Length);
        Assert.Contains(endpoints, endpoint =>
            endpoint.RoutePattern.RawText == "/api/reporting/v1/operational-metrics/shifts/query" &&
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "QueryShiftOperationalMetrics");
        Assert.Contains(endpoints, endpoint =>
            endpoint.RoutePattern.RawText == "/api/reporting/v1/operational-metrics/production-days/query" &&
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "QueryProductionDayOperationalMetrics");
        Assert.Contains(endpoints, endpoint =>
            endpoint.RoutePattern.RawText == "/api/reporting/v1/operational-metrics/production-day-shifts/query" &&
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "QueryProductionDayShiftOperationalMetrics");

        foreach (var endpoint in endpoints)
        {
            var tags = endpoint.Metadata.GetMetadata<ITagsMetadata>();
            Assert.NotNull(tags);
            Assert.Contains("Operational Metrics", tags.Tags);

            var responses = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
            var expectedResponseType = endpoint.RoutePattern.RawText ==
                "/api/reporting/v1/operational-metrics/production-day-shifts/query"
                ? typeof(ProductionDayShiftOperationalMetricPageResponse)
                : typeof(OperationalMetricPageResponse);
            Assert.Contains(responses, metadata =>
                metadata.StatusCode == StatusCodes.Status200OK &&
                metadata.Type == expectedResponseType);
            Assert.Contains(responses, metadata =>
                metadata.StatusCode == StatusCodes.Status400BadRequest);

            if (expectedResponseType == typeof(ProductionDayShiftOperationalMetricPageResponse))
            {
                Assert.Contains(responses, metadata =>
                    metadata.StatusCode == StatusCodes.Status409Conflict);
            }
        }
    }

    [Fact]
    public async Task GeneratedOpenApiDocumentContainsVersionedReportingOperations()
    {
        var reader = new StubQueryReader((_, _) => new ReportingPage<OperationalMetricQueryItem>([], null));
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");

        AssertOpenApiOperation(
            paths,
            "/api/reporting/v1/operational-metrics/shifts/query",
            "QueryShiftOperationalMetrics");
        AssertOpenApiOperation(
            paths,
            "/api/reporting/v1/operational-metrics/production-days/query",
            "QueryProductionDayOperationalMetrics");
        AssertOpenApiOperation(
            paths,
            "/api/reporting/v1/operational-metrics/production-day-shifts/query",
            "QueryProductionDayShiftOperationalMetrics",
            expectConflict: true);
    }

    private static void AssertOpenApiOperation(
        JsonElement paths,
        string path,
        string operationId,
        bool expectConflict = false)
    {
        Assert.True(paths.TryGetProperty(path, out var pathItem));
        var operation = pathItem.GetProperty("post");

        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());

        var responses = operation.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("400", out _));
        Assert.Equal(expectConflict, responses.TryGetProperty("409", out _));
    }

    private static OperationalMetricProjectionSummary CreateSummary(
        MachineId machineId,
        OperationalMetricProjectionProcessorId projectionProcessorId,
        OperationalMetricPeriodId periodId,
        OperationalMetricDefinitionId definitionId,
        OperationalMetricEvaluationContextKey context,
        OperationalMetricEvaluationStatus status,
        decimal? value,
        string unit,
        OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName,
        ulong position) =>
        new(
            projectionProcessorId,
            new OperationalMetricEvaluationKey(machineId, periodId, definitionId, context),
            status,
            value,
            unit,
            reasonCode,
            reasonOperandName,
            new MetricAggregationCheckpoint(
                new MetricAggregationProcessorId("aggregation"),
                new MetricInputStreamId(machineId, "metric-inputs"),
                new MetricInputPosition(position)));

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

    private sealed class StubQueryReader(
        Func<OperationalMetricReportQuery, CancellationToken, ReportingPage<OperationalMetricQueryItem>> read)
        : IOperationalMetricQueryReader
    {
        public ValueTask<ReportingPage<OperationalMetricQueryItem>> ReadAsync(
            OperationalMetricReportQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(read(query, cancellationToken));
    }

    private sealed class CancellationObservingReader : IOperationalMetricQueryReader
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public async ValueTask<ReportingPage<OperationalMetricQueryItem>> ReadAsync(
            OperationalMetricReportQuery query,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            _entered.TrySetResult();

            using var registration = cancellationToken.Register(() => _cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ReportingPage<OperationalMetricQueryItem>([], null);
        }
    }
}
