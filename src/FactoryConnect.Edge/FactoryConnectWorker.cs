namespace FactoryConnect.Edge;

public sealed class FactoryConnectWorker : BackgroundService
{
    private static readonly Action<ILogger, Exception?> EdgeStarting =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(EdgeStarting)),
            "FactoryConnect Edge starting.");
    private static readonly Action<ILogger, Exception?> AcquisitionFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(AcquisitionFailed)),
            "MTConnect acquisition runtime failed; its machine loop will retry independently.");

    private readonly IReadOnlyList<IMtConnectAcquisitionRuntimeFactory> _runtimeFactories;
    private readonly ILogger<FactoryConnectWorker> _logger;

    public FactoryConnectWorker(
        IEnumerable<IMtConnectAcquisitionRuntimeFactory> runtimeFactories,
        ILogger<FactoryConnectWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(runtimeFactories);
        ArgumentNullException.ThrowIfNull(logger);

        var snapshot = runtimeFactories.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one MTConnect acquisition runtime factory is required.",
                nameof(runtimeFactories));
        }

        _runtimeFactories = Array.AsReadOnly(snapshot);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        EdgeStarting(_logger, null);

        await Task.WhenAll(
            _runtimeFactories.Select(factory =>
                RunMachineAsync(factory, stoppingToken)));
    }

    private async Task RunMachineAsync(
        IMtConnectAcquisitionRuntimeFactory runtimeFactory,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runtime = await runtimeFactory.CreateAsync(stoppingToken);
                await runtime.RunAsync(stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AcquisitionFailed(_logger, exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
