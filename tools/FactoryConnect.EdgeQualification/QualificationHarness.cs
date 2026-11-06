using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryConnect.EdgeQualification;

/// <summary>Local-only qualification executable. Never registered by production Edge.</summary>
public static class QualificationHarness
{
    public const string Server = @"DESKTOP-1PJK2OB\FCINVENTORY";
    public const string Database = "FactoryConnect_P0_Qualification_20261106";
    private const string Gg = "677b030b-1ede-4a67-82ae-6a494791357f";
    private const string Moxa = "bfa0dc97-9a68-45ed-b189-1dacb7d863f0";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void ValidateTarget(string connectionString)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(settings.DataSource, Server, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.InitialCatalog, Database, StringComparison.Ordinal) ||
            !settings.IntegratedSecurity || settings.AttachDBFilename.Length != 0 ||
            settings.FailoverPartner.Length != 0 || settings.UserID.Length != 0 || settings.Password.Length != 0)
            throw new InvalidOperationException("Only the exact local disposable database with Windows authentication is permitted.");
    }

    public static void DisableAcquisition(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var hosted = services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray();
        if (hosted.Any(item => item.ImplementationType is null))
            throw new InvalidOperationException("Uninspectable hosted-service registration; qualification stopped.");
        var acquisition = hosted.Where(item => item.ImplementationType == typeof(FactoryConnectWorker)).ToArray();
        if (acquisition.Length != 1)
            throw new InvalidOperationException("Expected exactly one MTConnect acquisition worker registration.");
        services.Remove(acquisition[0]);
        foreach (var required in new[] { typeof(DurableObservationProcessingWorker), typeof(ProductionMetricInputProcessingWorker),
                     typeof(MetricAggregationProcessingWorker), typeof(OperationalMetricProjectionProcessingWorker) })
        {
            if (!services.Any(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == required))
                throw new InvalidOperationException($"Normal processing worker missing: {required.Name}");
        }
    }

    public static IConfigurationRoot LoadConfiguration(string commissionedPath, string contentRoot, string connectionString)
    {
        ValidateTarget(connectionString);
        var configuration = new ConfigurationBuilder().SetBasePath(Path.GetFullPath(contentRoot))
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile(Path.GetFullPath(commissionedPath), optional: false, reloadOnChange: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "SqlServer",
                ["PersistenceProviders:SqlServer:ConnectionString"] = connectionString,
            }).Build();
        if (configuration.GetValue<bool>("DemoCanonicalInputs:Enabled"))
            throw new InvalidOperationException("Demo canonical inputs must be disabled.");
        var machines = configuration.GetSection("MTConnect:Machines").GetChildren().ToArray();
        if (machines.Length != 2 || !machines.Select(item => item["MachineId"]).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { Gg, Moxa }.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("The exact commissioned two-machine inventory is required.");
        // Environment and command-line providers are deliberately absent. The source JSON is unchanged.
        return configuration;
    }

    public static async Task<int> RunAsync(string commissionedPath, string contentRoot, int seconds, string reportPath)
    {
        if (seconds is < 10 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = Server, InitialCatalog = Database, IntegratedSecurity = true,
            Encrypt = true, TrustServerCertificate = true, ConnectTimeout = 10,
            ApplicationName = "FactoryConnect-P0-Qualification",
        }.ConnectionString;
        ValidateTarget(connectionString);
        var originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(commissionedPath)));
        using var configuration = (ConfigurationRoot)LoadConfiguration(commissionedPath, contentRoot, connectionString);
        // Reserve a new report before constructing any worker; existing evidence is never overwritten.
        await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var samples = new List<AuthoritySample>();
        var holds = new ConcurrentQueue<string>();
        var workers = Array.Empty<string>();
        var acquisitionRemoved = false;
        var outcome = "FAILED";
        string? error = null;
        var exitCode = 1;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 120));
        try
        {
            var before = await ReadAuthorityAsync(connectionString, budget.Token);
            RequireGgBaseline(before);
            samples.AddRange(before);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                DisableDefaults = true, Args = [], EnvironmentName = Environments.Production,
                ContentRootPath = Path.GetFullPath(contentRoot),
            });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddConfiguration(configuration);
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
            builder.Services.AddFactoryConnectEdgeApplication(builder.Configuration);
            DisableAcquisition(builder.Services);
            acquisitionRemoved = true;
            workers = builder.Services.Where(item => item.ServiceType == typeof(IHostedService))
                .Select(item => item.ImplementationType!.FullName!).ToArray();
            using var host = builder.Build();
            var pipelines = host.Services.GetRequiredService<DurableObservationProcessingPipelineSet>();
            pipelines.ChronologyHeld += hold =>
            {
                if (hold.Observation.StreamId.MachineId.Value == Guid.Parse(Gg) && hold.Observation.Position.Value == 513521 &&
                    hold.CommittedPosition?.Value == 513501 && hold.CommittedRevision?.Value == 4091)
                    holds.Enqueue(hold.ToDiagnosticString());
            };
            // Same schema/startup gate as production, without any migration command.
            await host.Services.GetRequiredService<IPersistenceStartupGate>().EnsureReadyAsync(budget.Token);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            try
            {
                await host.StartAsync(budget.Token);
                for (var sample = 0; sample < seconds; sample++)
                {
                    if (lifetime.ApplicationStopping.IsCancellationRequested)
                        throw new InvalidOperationException("Edge host stopped before the observation interval completed.");
                    await Task.Delay(TimeSpan.FromSeconds(1), budget.Token);
                    var current = await ReadAuthorityAsync(connectionString, budget.Token);
                    RequireGgBaseline(current);
                    samples.AddRange(current);
                }
                if (lifetime.ApplicationStopping.IsCancellationRequested)
                    throw new InvalidOperationException("Edge host stopped unexpectedly.");
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await host.StopAsync(shutdown.Token);
            }
            var after = await ReadAuthorityAsync(connectionString, budget.Token);
            RequireGgBaseline(after);
            samples.AddRange(after);
            if (holds.IsEmpty) throw new InvalidOperationException("Expected exact GG chronology hold was not observed.");
            var initialMoxa = before.Single(item => item.MachineId == Moxa);
            var finalMoxa = after.Single(item => item.MachineId == Moxa);
            var advanced = ulong.Parse(finalMoxa.Position, CultureInfo.InvariantCulture) > ulong.Parse(initialMoxa.Position, CultureInfo.InvariantCulture) &&
                ulong.Parse(finalMoxa.Revision, CultureInfo.InvariantCulture) > ulong.Parse(initialMoxa.Revision, CultureInfo.InvariantCulture);
            outcome = advanced ? "MOXA_ADVANCED_GG_HELD" : "INCONCLUSIVE_NO_MOXA_ADVANCEMENT";
            exitCode = advanced ? 0 : 3;
        }
        catch (Exception exception)
        {
            // Qualification reporting only: this process exits failed and never masks worker failures as a hold.
            error = exception.GetType().Name + ": " + exception.Message;
        }
        var finalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(commissionedPath)));
        if (originalHash != finalHash) { outcome = "FAILED_CONFIG_CHANGED"; exitCode = 1; }
        var projection = configuration.AsEnumerable().Where(item => item.Value is not null &&
            (item.Key.StartsWith("ObservationProcessing:", StringComparison.OrdinalIgnoreCase) ||
             item.Key.StartsWith("DemoCanonicalInputs:", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Value);
        await JsonSerializer.SerializeAsync(report, new
        {
            Outcome = outcome, Error = error, ProcessId = Environment.ProcessId, SecondsRequested = seconds,
            TargetDatabase = Database, AcquisitionHostedServiceRemoved = acquisitionRemoved, Workers = workers,
            DemoCanonicalInputsEnabled = configuration.GetValue<bool>("DemoCanonicalInputs:Enabled"),
            CommissionedFileHashBefore = originalHash, CommissionedFileHashAfter = finalHash,
            EffectiveObservationConfiguration = projection, Holds = holds.ToArray(), Samples = samples,
            QualificationScope = "Composed local host only. No synthetic input injected. No connection to preserved incident database. Not production deployment or per-cycle timing acceptance.",
        }, JsonOptions);
        Console.WriteLine($"Qualification outcome: {outcome}; report: {Path.GetFullPath(reportPath)}");
        return exitCode;
    }

    private static void RequireGgBaseline(AuthoritySample[] samples)
    {
        var gg = samples.Single(item => item.MachineId == Gg);
        if (gg.Position != "513501" || gg.Revision != "4091")
            throw new InvalidOperationException("GG activity authority differs from the accepted held baseline.");
        _ = samples.Single(item => item.MachineId == Moxa);
    }

    private static async Task<AuthoritySample[]> ReadAuthorityAsync(string connectionString, CancellationToken cancellationToken)
    {
        ValidateTarget(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = """
            SELECT CONVERT(varchar(36), MachineId), CONVERT(varchar(20), Position), CONVERT(varchar(20), ProjectionRevision)
            FROM dbo.MachineStateActivityAuthority
            WHERE StateProcessorId = N'machine-state-activity'
              AND ((MachineId = @GG AND StreamKeyBinary = @GGStream)
                OR (MachineId = @Moxa AND StreamKeyBinary = @MoxaStream));
            """;
        command.Parameters.Add("@GG", SqlDbType.UniqueIdentifier).Value = Guid.Parse(Gg);
        command.Parameters.Add("@Moxa", SqlDbType.UniqueIdentifier).Value = Guid.Parse(Moxa);
        command.Parameters.Add("@GGStream", SqlDbType.VarBinary, 512).Value = Encoding.BigEndianUnicode.GetBytes("mtconnect:DEWAS-SR-GG-003");
        command.Parameters.Add("@MoxaStream", SqlDbType.VarBinary, 512).Value = Encoding.BigEndianUnicode.GetBytes("mtconnect:DEWAS-SR-MOXA-001");
        var samples = new List<AuthoritySample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            samples.Add(new(DateTimeOffset.UtcNow, reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return samples.ToArray();
    }

    private sealed record AuthoritySample(DateTimeOffset CapturedAtUtc, string MachineId, string Position, string Revision);
}
