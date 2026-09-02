using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactoryConnect.Api.Reporting;

public static class OperationalMetricReportingServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryConnectOperationalMetricReporting(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails();
        services.AddExceptionHandler<OperationalMetricReportingExceptionHandler>();
        services.Configure<RouteHandlerOptions>(
            static options => options.ThrowOnBadRequest = true);
        services.TryAddSingleton<IOperationalMetricReportingQueryReader>(
            static provider => new OperationalMetricReportingQueryReader(
                provider.GetRequiredService<IOperationalMetricReportingQueryProvider>()));
        services.TryAddSingleton<IOperationalMetricQueryReader>(
            static provider => new OperationalMetricQueryReader(
                provider.GetRequiredService<IOperationalMetricReportingQueryReader>()));
        services.TryAddSingleton<IProductionDayShiftOperationalMetricReader>(
            static provider => new ProductionDayShiftOperationalMetricReader(
                provider.GetRequiredService<IMachineShiftOccurrenceRosterStore>(),
                provider.GetRequiredService<IOperationalMetricReportReader>()));
        services.TryAddSingleton<IProductionDayShiftOperationalMetricQueryReader>(
            static provider => new ProductionDayShiftOperationalMetricQueryReader(
                provider.GetRequiredService<IProductionDayShiftOperationalMetricReader>()));

        return services;
    }
}
