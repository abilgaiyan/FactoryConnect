using System.Net;
using System.Net.Http.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricReportingCompositionTests
{
    [Fact]
    public async Task DurableProjectionFlowsThroughComposedReaderAndHttpEndpoint()
    {
        var store = new InMemoryOperationalMetricProjectionStore();
        var machineId = new MachineId(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var processorId = new OperationalMetricProjectionProcessorId(
            "operational-metrics:machine-a:builtins-v1");
        var businessDate = new DateOnly(2026, 8, 29);
        var key = new OperationalMetricEvaluationKey(
            machineId,
            new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(new SiteId("site-a"), businessDate)),
            new OperationalMetricDefinitionId("OEE", "1.0"),
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var sourceRevision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("metric-aggregation:machine-a"),
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(42));
        var projection = new OperationalMetricProjection(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.875m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            sourceRevision);
        var checkpoint = new OperationalMetricProjectionCheckpoint(
            processorId,
            sourceRevision,
            new OperationalMetricProjectionBatchManifest([key]));

        await store.CommitAsync(
            new OperationalMetricProjectionCommit(
                processorId,
                null,
                checkpoint,
                [projection]),
            CancellationToken.None);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IOperationalMetricReportingQueryProvider>(store);
        builder.Services.AddFactoryConnectOperationalMetricReporting();

        await using var app = builder.Build();
        app.MapOperationalMetricReportingEndpoints();
        await app.StartAsync();

        Assert.IsType<OperationalMetricReportingQueryReader>(
            app.Services.GetRequiredService<IOperationalMetricReportingQueryReader>());
        Assert.IsType<OperationalMetricQueryReader>(
            app.Services.GetRequiredService<IOperationalMetricQueryReader>());

        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/production-days/query",
            new ProductionDayOperationalMetricQueryRequest(
                [new ReportingSourceRequest(machineId.Value, processorId.Value)],
                businessDate,
                businessDate.AddDays(1),
                [new OperationalMetricDefinitionRequest("OEE", "1.0")],
                null,
                ["calculated"],
                "period-ascending",
                25,
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<OperationalMetricPageResponse>();

        Assert.NotNull(page);
        Assert.Null(page.ContinuationToken);
        var item = Assert.Single(page.Items);
        Assert.Equal(machineId.Value, item.MachineId);
        Assert.Equal("production-day", item.Scope);
        Assert.Equal(processorId.Value, item.ProcessorId);
        Assert.Equal("OEE", item.MetricKey);
        Assert.Equal("1.0", item.DefinitionVersion);
        Assert.Equal("calculated", item.Status);
        Assert.Equal(0.875m, item.Value);
        Assert.Equal(OperationalMetricUnits.Ratio, item.Unit);
        Assert.Equal(42UL, item.SourceRevision.Position);
    }
}
