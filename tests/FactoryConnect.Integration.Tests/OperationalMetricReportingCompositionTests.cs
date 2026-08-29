using System.Net;
using System.Net.Http.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricReportingCompositionTests
{
    [Fact]
    public async Task ProductionProgramPreservesScopesMachineIsolationAndSeekPagination()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var providerServices = factory.Services.GetRequiredService<PersistenceProviderServices>();
        var store = Assert.IsType<InMemoryOperationalMetricProjectionStore>(
            providerServices.OperationalMetricProjectionStore);

        var machineA = new MachineId(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var machineB = new MachineId(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
        var processorA = new OperationalMetricProjectionProcessorId(
            "operational-metrics:machine-a:builtins-v1");
        var processorB = new OperationalMetricProjectionProcessorId(
            "operational-metrics:machine-b:builtins-v1");
        var firstDay = new DateOnly(2026, 8, 29);
        var secondDay = firstDay.AddDays(1);
        var shiftStart = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var shift = new ShiftOccurrenceId(
            new SiteId("site-a"),
            new ShiftScheduleAssignmentId("schedule-a"),
            new ShiftId("shift-a"),
            shiftStart,
            shiftStart.AddHours(8));

        await CommitAsync(
            store,
            processorA,
            machineA,
            42,
            [
                CreateProjection(
                    processorA,
                    machineA,
                    new OperationalMetricPeriodId.ProductionDay(
                        new ProductionDayId(new SiteId("site-a"), firstDay)),
                    0.81m,
                    42),
                CreateProjection(
                    processorA,
                    machineA,
                    new OperationalMetricPeriodId.ProductionDay(
                        new ProductionDayId(new SiteId("site-a"), secondDay)),
                    0.82m,
                    42),
                CreateProjection(
                    processorA,
                    machineA,
                    new OperationalMetricPeriodId.Shift(shift),
                    0.83m,
                    42),
            ]);
        await CommitAsync(
            store,
            processorB,
            machineB,
            51,
            [
                CreateProjection(
                    processorB,
                    machineB,
                    new OperationalMetricPeriodId.ProductionDay(
                        new ProductionDayId(new SiteId("site-a"), firstDay)),
                    0.91m,
                    51),
            ]);

        Assert.IsType<FactoryConnect.Core.Metrics.OperationalMetricReportingQueryReader>(
            factory.Services.GetRequiredService<IOperationalMetricReportingQueryReader>());
        Assert.IsType<FactoryConnect.Core.Metrics.OperationalMetricQueryReader>(
            factory.Services.GetRequiredService<IOperationalMetricQueryReader>());

        using var client = factory.CreateClient();
        var firstPage = await QueryProductionDaysAsync(
            client,
            machineA,
            processorA,
            firstDay,
            secondDay.AddDays(1),
            1,
            null);
        var firstItem = Assert.Single(firstPage.Items);
        Assert.Equal(machineA.Value, firstItem.MachineId);
        Assert.Equal(firstDay, firstItem.ProductionDay?.BusinessDate);
        Assert.Equal(0.81m, firstItem.Value);
        Assert.NotNull(firstPage.ContinuationToken);

        var secondPage = await QueryProductionDaysAsync(
            client,
            machineA,
            processorA,
            firstDay,
            secondDay.AddDays(1),
            1,
            firstPage.ContinuationToken);
        var secondItem = Assert.Single(secondPage.Items);
        Assert.Equal(machineA.Value, secondItem.MachineId);
        Assert.Equal(secondDay, secondItem.ProductionDay?.BusinessDate);
        Assert.Equal(0.82m, secondItem.Value);
        Assert.Null(secondPage.ContinuationToken);
        Assert.NotEqual(firstItem.ProductionDay, secondItem.ProductionDay);

        using var shiftResponse = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/shifts/query",
            new ShiftOperationalMetricQueryRequest(
                [new ReportingSourceRequest(machineA.Value, processorA.Value)],
                shiftStart,
                shiftStart.AddDays(1),
                [new OperationalMetricDefinitionRequest("OEE", "1.0")],
                null,
                ["calculated"],
                "period-ascending",
                25,
                null));

        Assert.Equal(HttpStatusCode.OK, shiftResponse.StatusCode);
        var shiftPage = await shiftResponse.Content
            .ReadFromJsonAsync<OperationalMetricPageResponse>();
        Assert.NotNull(shiftPage);
        var shiftItem = Assert.Single(shiftPage.Items);
        Assert.Equal("shift", shiftItem.Scope);
        Assert.Equal(machineA.Value, shiftItem.MachineId);
        Assert.Equal(shiftStart, shiftItem.Shift?.StartsAtUtc);
        Assert.Equal(0.83m, shiftItem.Value);
    }

    [Fact]
    public void SqlServerReportingSelectionFailsForMissingCapability()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] =
                    SqlServerPersistenceServiceCollectionExtensions.ProviderKey,
                ["PersistenceProviders:SqlServer:ConnectionString"] =
                    "Server=test;Database=FactoryConnect;Integrated Security=True",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddFactoryConnectPersistenceProviders(configuration);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectPersistence(
                configuration,
                PersistenceProviderCapabilities.Reporting));

        Assert.Contains("SQLSERVER", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(PersistenceProviderCapabilities.OperationalMetricReportingQuery),
            exception.Message,
            StringComparison.Ordinal);
    }

    private static async Task<OperationalMetricPageResponse> QueryProductionDaysAsync(
        HttpClient client,
        MachineId machineId,
        OperationalMetricProjectionProcessorId processorId,
        DateOnly from,
        DateOnly to,
        int pageSize,
        string? continuationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/reporting/v1/operational-metrics/production-days/query",
            new ProductionDayOperationalMetricQueryRequest(
                [new ReportingSourceRequest(machineId.Value, processorId.Value)],
                from,
                to,
                [new OperationalMetricDefinitionRequest("OEE", "1.0")],
                null,
                ["calculated"],
                "period-ascending",
                pageSize,
                continuationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content
            .ReadFromJsonAsync<OperationalMetricPageResponse>();
        Assert.NotNull(page);
        return page;
    }

    private static async Task CommitAsync(
        InMemoryOperationalMetricProjectionStore store,
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ulong position,
        IReadOnlyList<OperationalMetricProjection> projections)
    {
        var sourceRevision = SourceRevision(machineId, position);
        await store.CommitAsync(
            new OperationalMetricProjectionCommit(
                processorId,
                null,
                new OperationalMetricProjectionCheckpoint(
                    processorId,
                    sourceRevision,
                    new OperationalMetricProjectionBatchManifest(
                        projections.Select(static projection => projection.Key))),
                projections),
            CancellationToken.None);
    }

    private static OperationalMetricProjection CreateProjection(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        decimal value,
        ulong position) =>
        new(
            processorId,
            new OperationalMetricEvaluationKey(
                machineId,
                periodId,
                new OperationalMetricDefinitionId("OEE", "1.0"),
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            value,
            OperationalMetricUnits.Ratio,
            null,
            null,
            SourceRevision(machineId, position));

    private static MetricAggregationCheckpoint SourceRevision(
        MachineId machineId,
        ulong position) =>
        new(
            new MetricAggregationProcessorId(
                $"metric-aggregation:{machineId.Value:D}"),
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(position));
}
