using FactoryConnect.GateBControlledAgent;
using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using System.Globalization;
using System.Net;
using System.Text;

// An isolated protocol fixture. No SQL access, factory endpoint or production configuration.
if (args.Length == 1 && args[0] == "--rehearse-generation")
    return await GateBGenerationRehearsal.RunAsync();

if (args.Length == 1 && args[0] == "--preflight")
    return await GateBAdmissionPreflight.RunAsync();

const ulong fixtureInstance = 9000001;
const int port = 51027;
const string device = "DEWAS-SR-MOXA-001";
if (args.Length != 3 || (args[0] != "--serve" && args[0] != "--probe") || args[1] != "--phase" ||
    (args[2] != "first" && args[2] != "second"))
{
    Console.Error.WriteLine("Usage: GateBControlledAgent --preflight|--rehearse-generation OR --serve|--probe --phase first|second");
    return 2;
}

var phase = args[2];
if (args[0] == "--probe")
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var client = new MtConnectSampleClient(http);
        var endpoint = new MtConnectEndpoint(new Uri("http://127.0.0.1:51027/"));
        var machine = new MachineId(Guid.Parse("bfa0dc97-9a68-45ed-b189-1dacb7d863f0"));
        const string deviceKey = "DEWAS-SR-MOXA-001";
        var from = phase == "first" ? 27UL : 2UL;
        var result = await client.AcquireAsync(endpoint, machine, deviceKey, from);
        var expectedSequence = phase == "first" ? 1UL : 2UL;
        var expectedStatus = phase == "first" ? "RUNNING" : "IDLE";
        var item = result.Observations.Single();
        if (result.InstanceId != fixtureInstance || result.FirstSequence != 1 ||
            result.NextSequence != expectedSequence + 1 || item.Sequence != expectedSequence ||
            item.Observation.MachineId != machine ||
            item.Observation.Source != "mtconnect" ||
            item.Observation.Address != "moxa001_status" ||
            item.Observation.Type != SignalType.Enumeration ||
            item.Observation.Quality != ObservationQuality.Good ||
            !string.Equals(item.Observation.Value?.ToString(), expectedStatus, StringComparison.Ordinal))
            throw new InvalidDataException("Controlled Agent response did not match the expected production parser contract.");
        Console.WriteLine($"PASS: production MTConnect parser accepted {phase} phase, instance={result.InstanceId}, sequence={item.Sequence}, status={expectedStatus}.");
        Console.WriteLine("READ ONLY: no SQL writer, processing host, or production endpoint used.");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
        return 1;
    }
}

using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine($"Gate B controlled MTConnect fixture listening at http://127.0.0.1:{port}/");
Console.WriteLine($"Phase={phase}. ISOLATED SOURCE ONLY. No SQL writer or Edge worker is started. Ctrl+C to stop.");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); listener.Stop(); };
try
{
    while (!shutdown.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); }
        catch (HttpListenerException) when (shutdown.IsCancellationRequested) { break; }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { break; }

        var request = context.Request;
        var path = request.Url?.AbsolutePath;
        var fromText = request.QueryString["from"];
        var validFrom = ulong.TryParse(fromText, NumberStyles.None, CultureInfo.InvariantCulture, out var from);
        var valid = request.HttpMethod == "GET" && path == "/sample" && validFrom &&
                    (phase == "first" ? from is 27 or 1 : from == 2);
        context.Response.StatusCode = valid ? 200 : 400;
        context.Response.ContentType = "application/xml; charset=utf-8";
        // The first response intentionally exposes a new instance at a previously
        // retained cursor. Production recovery must report the discontinuity and
        // retry from firstSequence=1 before it can durably publish anything.
        var sequence = from == 2 ? 2UL : 1UL;
        var status = sequence == 1 ? "RUNNING" : "IDLE";
        var timestamp = sequence == 1 ? "2026-11-05T08:36:22Z" : "2026-11-05T08:36:23Z";
        var xml = valid ? $"""
            <?xml version="1.0" encoding="utf-8"?>
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.5">
              <Header instanceId="{fixtureInstance}" firstSequence="1" lastSequence="{sequence}" nextSequence="{sequence + 1}" />
              <Streams>
                <DeviceStream name="{device}">
                  <ComponentStream component="Controller" componentId="moxa001">
                    <Events>
                      <Execution dataItemId="moxa001_status" timestamp="{timestamp}" sequence="{sequence}">{status}</Execution>
                    </Events>
                  </ComponentStream>
                </DeviceStream>
              </Streams>
            </MTConnectStreams>
            """ : "<error>Unsupported request for selected fixture phase</error>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} {request.HttpMethod} {request.RawUrl} -> {context.Response.StatusCode} (fixture instance {fixtureInstance})");
    }
}
finally { if (listener.IsListening) listener.Stop(); }
return 0;
