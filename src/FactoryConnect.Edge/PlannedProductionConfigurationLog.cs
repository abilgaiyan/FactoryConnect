using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryConnect.Edge;

internal sealed record LoadedPlannedProductionConfiguration(string Payload);

// Emitted from the validated objects supplied to the runtime reader, not a file capture.
internal sealed partial class PlannedProductionConfigurationLog(
    LoadedPlannedProductionConfiguration configuration,
    ILogger<PlannedProductionConfigurationLog> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogLoaded(logger, configuration.Payload);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 731, Level = LogLevel.Information,
        Message = "Loaded planned-production assignments (not a processing acknowledgement): {Assignments}")]
    private static partial void LogLoaded(ILogger logger, string assignments);
}
