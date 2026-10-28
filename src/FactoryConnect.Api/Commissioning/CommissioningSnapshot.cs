using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Api.Commissioning;

public sealed record CommissioningField(string Path, string? Value);
public sealed record CommissioningConfiguration(string Status, string Authority,
    DateTimeOffset? FileModifiedAt, string? Sha256, CommissioningField[] Fields);
public sealed record CommissioningDurable(string Status, string Authority,
    DateTimeOffset? ReadCompletedAt, CommissioningProcessor[] Processors);
public sealed record CommissioningSnapshot(DateTimeOffset ReadStartedAt,
    CommissioningConfiguration Configuration, CommissioningDurable Durable,
    string RuntimeActivation, string TransportHealth, string CheckpointFreshness);

public static class CommissioningAccess
{
    // Local review only. Do not enable behind a reverse proxy or port forward.
    public static bool Allows(HttpContext context, IConfiguration configuration) =>
        configuration.GetValue<bool>("Commissioning:Enabled") &&
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address) &&
        !context.Request.Headers.ContainsKey("Forwarded") &&
        !context.Request.Headers.Keys.Any(static key => key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));
}

public sealed class CommissioningSnapshotReader(IConfiguration configuration) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public void Dispose()
    {
        gate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<CommissioningSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            var started = DateTimeOffset.UtcNow;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var captured = await ReadConfigurationAsync(timeout.Token).ConfigureAwait(false);
            var durable = new CommissioningDurable("unavailable", "SQL production-context processor/checkpoint heads", null, []);
            var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    var rows = await CommissioningProcessorReader.ReadAsync(connectionString, timeout.Token).ConfigureAwait(false);
                    durable = durable with { Status = "read", ReadCompletedAt = DateTimeOffset.UtcNow, Processors = rows.Select(static row => row with
                    { ProcessorId = PublicValue(row.ProcessorId)!, StreamKey = PublicValue(row.StreamKey)! }).ToArray() };
                }
                catch (Exception exception) when (exception is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or ArgumentException or OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    durable = durable with { Status = "failed" };
                }
            }
            return new CommissioningSnapshot(started, captured, durable,
                "unknown/unverified", "unknown: use current-state contact evidence separately",
                "unknown: heads do not record last-advance time");
        }
        finally { gate.Release(); }
    }

    private async Task<CommissioningConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        var result = new CommissioningConfiguration("unavailable", "captured Edge JSON file; not effective runtime configuration", null, null, []);
        var path = configuration["Commissioning:CapturedEdgeConfigurationPath"];
        if (string.IsNullOrWhiteSpace(path)) return result;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 1_048_576) return result with { Status = "limit-exceeded" };
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var fields = Project(document.RootElement);
            return result with { Status = "captured-file-only", FileModifiedAt = File.GetLastWriteTimeUtc(path),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Fields = fields };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return result with { Status = "failed" };
        }
    }

    // A closed schema projection; no raw configuration, URLs, logs or provider values escape.
    public static CommissioningField[] Project(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Object required.");
        List<CommissioningField> fields = [];
        Walk(root, "", fields);
        return fields.ToArray();
    }

    private static void Walk(JsonElement node, string path, List<CommissioningField> fields)
    {
        if (fields.Count > 2048) throw new InvalidOperationException("Projection limit exceeded.");
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate configuration key.");
                var next = path.Length == 0 ? property.Name : path + ":" + property.Name;
                if (AllowedPath(next)) Walk(property.Value, next, fields);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            if (node.GetArrayLength() > 128) throw new InvalidOperationException("Collection limit exceeded.");
            var index = 0;
            foreach (var item in node.EnumerateArray())
                Walk(item, path + ":" + (index++).ToString(CultureInfo.InvariantCulture), fields);
        }
        else
        {
            var value = node.ValueKind == JsonValueKind.Null ? null : node.ToString();
            fields.Add(new CommissioningField(path, PublicValue(value)));
        }
    }

    private static bool AllowedPath(string path)
    {
        var normalized = string.Join(':', path.Split(':').Where(static part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)));
        return Paths.Contains(normalized) || Paths.Any(candidate => candidate.StartsWith(normalized + ":", StringComparison.Ordinal));
    }

    private static readonly HashSet<string> Paths = BuildPaths();
    private static HashSet<string> BuildPaths()
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        static void Add(HashSet<string> target, string prefix, params string[] names)
        { foreach (var name in names) target.Add(prefix + ":" + name); }
        foreach (var prefix in new[] { "MTConnect", "MTConnect:Machines" })
            Add(paths, prefix, "MachineId", "DeviceKey", "PollingInterval");
        Add(paths, "ObservationProcessing:Streams", "MachineId", "StreamKey");
        foreach (var owner in new[] { "ObservationProcessing", "ObservationProcessing:Streams" })
        {
            Add(paths, owner + ":Mappings", "Source", "Address", "SignalKey", "Type", "Invert");
            foreach (var normalization in new[] { "ExecutionNormalization", "MoxaStatusNormalization" })
                Add(paths, owner + ":" + normalization, "Source", "Address");
        }
        foreach (var prefix in new[] { "ProductionProcessing", "ProductionProcessing:Machines" })
        {
            Add(paths, prefix, "MachineId", "CompanyId", "SiteId", "ProductionLineId", "ActivityStreamKey", "QuantityStreamKey", "ProductionStartsAt");
            Add(paths, prefix + ":Contexts", "AssignmentId", "EffectiveFrom", "EffectiveTo");
            Add(paths, prefix + ":PlannedProduction", "AssignmentId", "TimeZoneId", "EffectiveFrom", "EffectiveTo", "ActiveDays", "StartsAtLocal", "EndsAtLocal");
            foreach (var windows in new[] { "PlannedWindows", "BreakWindows" })
                Add(paths, prefix + ":PlannedProduction:" + windows, "StartsAtLocal", "EndsAtLocal", "StartDayOffset");
        }
        Add(paths, "ProductionProcessing:ShiftSchedules", "AssignmentId", "CompanyId", "SiteId", "ProductionLineId", "TimeZoneId", "ShiftId", "Name", "StartsAtLocal", "EndsAtLocal", "StartDayOffset", "ActiveDays", "EffectiveFrom", "EffectiveTo");
        return paths;
    }

    public static string? PublicValue(string? value) => value is null ? null :
        value.Length > 256 || value.Contains("://", StringComparison.Ordinal) ||
        value.Contains('=') || value.Contains(';') || value.Contains('@') || value.Contains('\\') ||
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase)
            ? "[redacted]" : value;
}

public static class CommissioningEndpoints
{
    public static void MapCommissioning(this WebApplication app)
    {
        app.MapGet("/api/commissioning/v1/snapshot", async (HttpContext context,
            IConfiguration configuration, CommissioningSnapshotReader reader) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!CommissioningAccess.Allows(context, configuration)) return Results.NotFound();
            var snapshot = await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false);
            return snapshot is null ? Results.StatusCode(429) : Results.Ok(snapshot);
        }).ExcludeFromDescription();
    }
}
