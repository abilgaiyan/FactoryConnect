using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryConnect.GateBControlledAgent;

/// <summary>
/// Constructs the production acquisition pipeline against the isolated clone.
/// Admission is intentionally disabled until SQL source-generation qualification is complete.
/// </summary>
internal static class GateBBoundedAcquisition
{
    public static async Task<int> RunAsync(bool verifyDenial = false)
    {
        if (await GateBAdmissionPreflight.RunAsync() != 0)
            return 1;

        var connectionString = Environment.GetEnvironmentVariable("FC_GATEB_SQL_CONNECTION")
            ?? throw new InvalidOperationException("SQL connection was lost after preflight.");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "SqlServer",
                ["PersistenceProviders:SqlServer:ConnectionString"] = connectionString,
                ["ConnectionString"] = connectionString,
            }).Build();
        var services = new ServiceCollection();
        services.AddSqlServerPersistenceProvider(configuration);
        services.AddFactoryConnectPersistence(configuration, PersistenceProviderCapabilities.Core);
        using var provider = services.BuildServiceProvider();
        // Use the fixture-only transaction policy; the continuity reporter still denies\n        // generation changes before the sink, so no SQL write is authorized.\n        IObservationIngestionStore store = new SqlServerObservationIngestionStore(\n            connectionString, new GateBIngestionTransactionAdmission());

        var machine = new MachineId(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0"));
        const string device = "DEWAS-SR-MOXA-001";
        var stream = MtConnectObservationStreamId.Create(machine, device);
        var checkpoint = await store.ReadCheckpointAsync(stream);
        if (checkpoint is null || checkpoint.InstanceId != 1793867780UL ||
            checkpoint.NextSequence != 27UL)
            throw new InvalidDataException("Acquisition store checkpoint differs from approved preflight.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var sample = new MtConnectSampleClient(http);
        var sessionFactory = new MtConnectAcquisitionSessionFactory(sample);
        var retry = new MtConnectTransientRetryPolicy(
            new MtConnectRetryOptions(1, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 0),
            new NoRetryDelay(), new FixedJitter(), NullLogger<MtConnectTransientRetryPolicy>.Instance);
        var recovery = new MtConnectContinuityRecoveryPolicy(
            sessionFactory, new MtConnectCurrentClient(http), retry, new DenyUnapprovedContinuity());
        var sink = new MtConnectDurableObservationSink(store, stream);
        var options = new MtConnectAcquisitionOptions(
            new MtConnectEndpoint(new Uri("http://127.0.0.1:51027/")),
            machine, device, 27UL, TimeSpan.FromSeconds(1));
        var factory = new MtConnectAcquisitionRuntimeFactory(
            options, new MtConnectStartupCheckpointResolver(store),
            sessionFactory, retry, recovery, sink, TimeProvider.System);
        var runtime = (MtConnectAcquisitionRuntime)await factory.CreateAsync();
        if (verifyDenial)
        {
            var denied = false;
            try
            {
                _ = await runtime.RunCycleAsync();
            }
            catch (InvalidOperationException error)
                when (error.Message == "SQL source-generation admission is not authorized.")
            {
                denied = true;
            }

            if (!denied)
                throw new InvalidDataException("Expected source-generation denial was not observed.");

            if (await GateBAdmissionPreflight.RunAsync() != 0)
                throw new InvalidDataException("Fixture baseline changed during denied acquisition.");

            Console.WriteLine("PASS: real bounded acquisition rejected source-generation change before SQL sink; fixture baseline unchanged.");
            Console.WriteLine("READ ONLY: source-generation admission denied; no durable commit.");
            return 0;
        }

        Console.WriteLine("PASS: production SQL ingestion store, startup resolver, continuity policy, durable sink and bounded runtime constructed.");
        Console.WriteLine("BLOCKED: source-generation SQL admission not qualified; RunCycleAsync was NOT invoked. No writes performed.");
        return 0;
    }

    private sealed class DenyUnapprovedContinuity : IMtConnectContinuityReporter
    {
        public ValueTask ReportAsync(MtConnectContinuityLoss loss, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SQL source-generation admission is not authorized.");
    }

    private sealed class NoRetryDelay : IMtConnectRetryDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected retry.");
    }

    private sealed class FixedJitter : IMtConnectJitterSource
    {
        public double NextDouble() => 0.5;
    }
}
