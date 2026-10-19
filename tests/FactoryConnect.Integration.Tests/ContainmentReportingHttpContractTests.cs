using System.Net.Http.Json;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class ContainmentReportingHttpContractTests
{
    private static readonly MachineId Machine = new(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f"));
    private static readonly OperationalMetricProjectionProcessorId Processor = AvailabilityContainmentV1.ProjectionProcessorId(Machine);
    private static readonly SiteId Site = new("http-contract");
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
    private static readonly ShiftOccurrenceId Shift = new(Site, new ShiftScheduleAssignmentId("schedule"), new ShiftId("shift"), Start, Start.AddHours(8));
    private static readonly ProductionDayId Day = new(Site, new DateOnly(2026, 10, 18));

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public async Task ExplicitEvidenceFailuresMapAcrossAllRoutesWithoutLeakingDetails(int failure, int scope)
    {
        Exception error = failure switch
        {
            0 => new ContainmentHistoricalIntegrityException("private SQL evidence detail"),
            1 => new UnsupportedContainmentCodecException("FCDS", 99),
            2 => new MissingContainmentHistoricalAuthorityException("private SQL authority detail"),
            _ => new NotSupportedException("arbitrary runtime failure is not an evidence codec classification")
        };
        await using var app = await App(new Reader(error));
        using var response = await Send(app, scope);
        Assert.Equal(failure switch { 1 => 501, 2 => 503, _ => 500 }, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private SQL", body, StringComparison.Ordinal);
        Assert.DoesNotContain("arbitrary runtime", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("items", out _));
        if (failure < 3) Assert.Equal(failure switch { 0 => "reporting-evidence-integrity", 1 => "unsupported-reporting-codec", _ => "reporting-authority-unavailable" }, json.RootElement.GetProperty("code").GetString());
        else Assert.False(json.RootElement.TryGetProperty("code", out _));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public async Task ProvenanceDistinguishesLegacyContainmentAndSynthesizedMissingCells(int kind, int scope)
    {
        OperationalMetricPublicationMetadata? metadata = kind switch
        {
            0 => OperationalMetricPublicationMetadata.Legacy,
            1 => new(2, AvailabilityContainmentV1.PolicyId, new string('A', 64), new string('B', 64)),
            _ => null
        };
        await using var app = await App(new Reader(null, metadata));
        using var response = await Send(app, scope);
        Assert.Equal(200, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement.GetProperty("items")[0];
        if (scope == 2) item = item.GetProperty("metrics")[0];
        Assert.Equal(kind == 2 ? (int?)null : kind == 0 ? 1 : 2, item.GetProperty("publicationFormatVersion").GetInt32OrNull());
        Assert.Equal(metadata?.EvaluationPolicyId, item.GetProperty("evaluationPolicyId").GetString());
        Assert.Equal(metadata?.ResultId, item.GetProperty("resultId").GetString());
        Assert.Equal(metadata?.ConflictWitnessId, item.GetProperty("conflictWitnessId").GetString());
    }

    private sealed class Reader(Exception? error, OperationalMetricPublicationMetadata? metadata = null)
        : IOperationalMetricQueryReader, IProductionDayShiftOperationalMetricQueryReader
    {
        private OperationalMetricProjectionSummary Summary(OperationalMetricPeriodId period) => new(Processor,
            new(Machine, period, new(CanonicalMetricKeys.Availability, "1.0"), OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Unavailable, null, "ratio", OperationalMetricEvaluationReasonCode.MissingOperand,
            "ActualProductionTime", new(new("aggregation"), MetricInputStreamId.ForMachine(Machine), new(1)), metadata);
        public ValueTask<ReportingPage<OperationalMetricQueryItem>> ReadAsync(OperationalMetricReportQuery query, CancellationToken cancellationToken)
        {
            if (error is not null) throw error;
            var summary = Summary(query is ShiftOperationalMetricReportQuery ? new OperationalMetricPeriodId.Shift(Shift) : new OperationalMetricPeriodId.ProductionDay(Day));
            return ValueTask.FromResult(new ReportingPage<OperationalMetricQueryItem>([OperationalMetricQueryItem.FromSummary(summary)], null));
        }
        public ValueTask<ReportingPage<ProductionDayShiftOperationalMetricReport>> ReadAsync(ProductionDayShiftOperationalMetricPageQuery query, CancellationToken cancellationToken)
        {
            if (error is not null) throw error;
            var summary = Summary(new OperationalMetricPeriodId.Shift(Shift));
            return ValueTask.FromResult(new ReportingPage<ProductionDayShiftOperationalMetricReport>([new(new(Machine, Processor), Day,
                new("line"), Shift, OperationalMetricEvaluationContextKey.Unpartitioned, summary.SourceRevision, [new(summary)])], null));
        }
    }
    private static async Task<WebApplication> App(Reader reader)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IOperationalMetricQueryReader>(reader);
        builder.Services.AddSingleton<IProductionDayShiftOperationalMetricQueryReader>(reader);
        builder.Services.AddFactoryConnectOperationalMetricReporting();
        var app = builder.Build(); app.UseExceptionHandler(); app.MapOperationalMetricReportingEndpoints(); await app.StartAsync(); return app;
    }
    private static Task<HttpResponseMessage> Send(WebApplication app, int scope) => scope switch
    {
        0 => app.GetTestClient().PostAsJsonAsync("/api/reporting/v1/operational-metrics/shifts/query",
            new ShiftOperationalMetricQueryRequest([new(Machine.Value, Processor.Value)], Start, Start.AddHours(8), null, null, null, "period-ascending", 100, null)),
        1 => app.GetTestClient().PostAsJsonAsync("/api/reporting/v1/operational-metrics/production-days/query",
            new ProductionDayOperationalMetricQueryRequest([new(Machine.Value, Processor.Value)], Day.BusinessDate, Day.BusinessDate.AddDays(1), null, null, null, "period-ascending", 100, null)),
        _ => app.GetTestClient().PostAsJsonAsync("/api/reporting/v1/operational-metrics/production-day-shifts/query",
            new ProductionDayShiftOperationalMetricQueryRequest([new(Machine.Value, Processor.Value, Site.Value, Day.BusinessDate)], null, null, null, 100, null))
    };
}

internal static class ReportingJsonValueExtensions
{
    internal static int? GetInt32OrNull(this JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();
}
