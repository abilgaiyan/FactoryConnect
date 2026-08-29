using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
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
        services.TryAddSingleton<IOperationalMetricReportingQueryReader>(
            static provider => new OperationalMetricReportingQueryReader(
                provider.GetRequiredService<IOperationalMetricReportingQueryProvider>()));
        services.TryAddSingleton<IOperationalMetricQueryReader>(
            static provider => new OperationalMetricQueryReader(
                provider.GetRequiredService<IOperationalMetricReportingQueryReader>()));

        return services;
    }
}
