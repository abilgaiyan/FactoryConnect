using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class ProductionDayShiftOperationalMetricEndpointTests
{
    [Fact]
    public async Task EndpointBindsExactProductionDaySelectionAndPreservesAuthoritativeReport()
    {
        var machineId = new MachineId(Guid.Parse("66666666-6666-6666-6666-666666666666"));
        var processorId = new OperationalMetricProjectionProcessorId("projection-shifts");
        var siteId = new SiteId("site-shifts");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 9, 1));
        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("order-42"),
            OperationId = new OperationId("operation-10"),
        };
        var occurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("assignment-a"),
            new ShiftId("shift-a"),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var revision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregation"),
            new MetricInputStreamId(machineId, "metric-inputs"),
            new MetricInputPosition(73));
        var definitionId = new OperationalMetricDefinitionId("OEE", "1.0");
        var summary = new OperationalMetricProjectionSummary(
            processorId,
            new OperationalMetricEvaluationKey(
                machineId,
                new OperationalMetricPeriodId.Shift(occurrence),
                definitionId,
                context),
            OperationalMetricEvaluationStatus.Calculated,
            0.37m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision);
        var report = new ProductionDayShiftOperationalMetricReport(
            new OperationalMetricReportingSource(machineId, processorId),
            day,
            new ProductionLineId("line-1"),
            occurrence,
            context,
            revision,
            [new OperationalMetricReportItem(summary)]);
        ProductionDayShiftOperationalMetricPageQuery? captured = null;
        var reader = new StubReader((query, _) =>
        {
            captured = query;
            return new ReportingPage<ProductionDayShiftOperationalMetricReport>(
                [report],
                new ReportingContinuationToken("opaque-next"));
        });

        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();
        var request = new ProductionDayShiftOperationalMetricQueryRequest(
            [new ProductionDayShiftReportingSourceRequest(
                machineId.Value,
                processorId.Value,
                siteId.Value,
                day.BusinessDate)],
            new OperationalMetricContextRequest(
                "order-42",
                "operation-10",
                null,
                null),
            [new OperationalMetricDefinitionRequest("OEE", "1.0")],
            ["calculated"],
            17,
            "opaque-input");

        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/production-day-shifts/query",
            request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal(17, captured.Page.PageSize);
        Assert.Equal("opaque-input", captured.Page.ContinuationToken?.Value);
        var selection = Assert.Single(captured.Selection.Sources);
        Assert.Equal(machineId, selection.Source.MachineId);
        Assert.Equal(processorId, selection.Source.ProcessorId);
        Assert.Equal(day, selection.ProductionDayId);
        Assert.Equal(context, captured.Selection.ContextKey);
        Assert.Equal(definitionId, Assert.Single(captured.Selection.Metrics!.DefinitionIds));
        Assert.Equal(
            OperationalMetricEvaluationStatus.Calculated,
            Assert.Single(captured.Selection.Statuses!.Statuses));

        var body = await response.Content.ReadFromJsonAsync<ProductionDayShiftOperationalMetricPageResponse>();
        Assert.NotNull(body);
        Assert.Equal("opaque-next", body.ContinuationToken);
        var item = Assert.Single(body.Items);
        Assert.Equal(machineId.Value, item.MachineId);
        Assert.Equal(processorId.Value, item.ProcessorId);
        Assert.Equal(siteId.Value, item.ProductionDay.SiteId);
        Assert.Equal(day.BusinessDate, item.ProductionDay.BusinessDate);
        Assert.Equal("line-1", item.ProductionLineId);
        Assert.Equal("assignment-a", item.Shift.ShiftScheduleAssignmentId);
        Assert.Equal("shift-a", item.Shift.ShiftId);
        Assert.Equal(occurrence.StartsAtUtc, item.Shift.StartsAtUtc);
        Assert.Equal(occurrence.EndsAtUtc, item.Shift.EndsAtUtc);
        Assert.Equal("order-42", item.Context.ProductionOrderId);
        Assert.Equal("operation-10", item.Context.OperationId);
        Assert.NotNull(item.SourceRevision);
        Assert.Equal("aggregation", item.SourceRevision.ProcessorId);
        Assert.Equal(73UL, item.SourceRevision.Position);
        var metric = Assert.Single(item.Metrics);
        Assert.Equal("OEE", metric.MetricKey);
        Assert.Equal("1.0", metric.DefinitionVersion);
        Assert.Equal("calculated", metric.Status);
        Assert.Equal(0.37m, metric.Value);
    }

    [Fact]
    public async Task ZeroEvidenceOccurrenceRemainsVisibleWithNullRevisionAndEmptyMetrics()
    {
        var machineId = new MachineId(Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var processorId = new OperationalMetricProjectionProcessorId("projection-empty");
        var siteId = new SiteId("site-empty");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 9, 2));
        var occurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("assignment-empty"),
            new ShiftId("shift-empty"),
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
        var report = new ProductionDayShiftOperationalMetricReport(
            new OperationalMetricReportingSource(machineId, processorId),
            day,
            new ProductionLineId("line-empty"),
            occurrence,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            null,
            []);
        var reader = new StubReader((_, _) =>
            new ReportingPage<ProductionDayShiftOperationalMetricReport>([report], null));

        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();
        var request = new ProductionDayShiftOperationalMetricQueryRequest(
            [new ProductionDayShiftReportingSourceRequest(
                machineId.Value,
                processorId.Value,
                siteId.Value,
                day.BusinessDate)],
            null,
            null,
            null,
            10,
            null);

        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/production-day-shifts/query",
            request);
        var body = await response.Content.ReadFromJsonAsync<ProductionDayShiftOperationalMetricPageResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        var item = Assert.Single(body.Items);
        Assert.Null(item.SourceRevision);
        Assert.Empty(item.Metrics);
        Assert.Null(item.Context.ProductionOrderId);
        Assert.Null(item.Context.OperationId);
        Assert.Null(item.Context.PartId);
        Assert.Null(item.Context.OperatorId);
    }

    [Fact]
    public async Task MissingRosterCoverageReturnsTypedConflictProblem()
    {
        var machineId = new MachineId(Guid.Parse("88888888-8888-8888-8888-888888888888"));
        var day = new ProductionDayId(new SiteId("site-missing"), new DateOnly(2026, 9, 3));
        var reader = new ThrowingReader(new ProductionDayShiftRosterCoverageRequiredException(machineId, day));

        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();
        var request = new ProductionDayShiftOperationalMetricQueryRequest(
            [new ProductionDayShiftReportingSourceRequest(
                machineId.Value,
                "projection-missing",
                day.SiteId.Value,
                day.BusinessDate)],
            null,
            null,
            null,
            10,
            null);

        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/production-day-shifts/query",
            request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            "urn:factoryconnect:problem:reporting:production-day-shift-roster-coverage-required",
            root.GetProperty("type").GetString());
        Assert.Equal(
            "production-day-shift-roster-coverage-required",
            root.GetProperty("code").GetString());
        Assert.Equal(machineId.Value, root.GetProperty("machineId").GetGuid());
        Assert.Equal(day.SiteId.Value, root.GetProperty("siteId").GetString());
        Assert.Equal("2026-09-03", root.GetProperty("businessDate").GetString());
    }

    [Fact]
    public async Task ContradictoryUnpartitionedContextIsRejectedBeforeReaderAccess()
    {
        var reader = new CountingReader();
        await using var app = await CreateAppAsync(reader);
        using var client = app.GetTestClient();
        var request = new ProductionDayShiftOperationalMetricQueryRequest(
            [new ProductionDayShiftReportingSourceRequest(
                Guid.Parse("99999999-9999-9999-9999-999999999999"),
                "projection-invalid",
                "site-invalid",
                new DateOnly(2026, 9, 4))],
            new OperationalMetricContextRequest(
                "order-invalid",
                null,
                null,
                null,
                UnpartitionedOnly: true),
            null,
            null,
            10,
            null);

        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/production-day-shifts/query",
            request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, reader.ReadCount);
    }

    private static async Task<WebApplication> CreateAppAsync(
        IProductionDayShiftOperationalMetricQueryReader reader)
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

    private sealed class StubReader(
        Func<ProductionDayShiftOperationalMetricPageQuery, CancellationToken, ReportingPage<ProductionDayShiftOperationalMetricReport>> read)
        : IProductionDayShiftOperationalMetricQueryReader
    {
        public ValueTask<ReportingPage<ProductionDayShiftOperationalMetricReport>> ReadAsync(
            ProductionDayShiftOperationalMetricPageQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(read(query, cancellationToken));
    }

    private sealed class ThrowingReader(Exception exception) : IProductionDayShiftOperationalMetricQueryReader
    {
        public ValueTask<ReportingPage<ProductionDayShiftOperationalMetricReport>> ReadAsync(
            ProductionDayShiftOperationalMetricPageQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ReportingPage<ProductionDayShiftOperationalMetricReport>>(exception);
    }

    private sealed class CountingReader : IProductionDayShiftOperationalMetricQueryReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<ReportingPage<ProductionDayShiftOperationalMetricReport>> ReadAsync(
            ProductionDayShiftOperationalMetricPageQuery query,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(new ReportingPage<ProductionDayShiftOperationalMetricReport>([], null));
        }
    }
}
