using System.Globalization;
using System.Text.Json;
using FactoryConnect.ConfigurationPreflight;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: <candidate.json> <Edge release directory> <business-date yyyy-MM-dd>");
    return 2;
}
try
{
    var result = await G2ConfigurationPreflight.ValidateAsync(
        Path.GetFullPath(args[0]), Path.GetFullPath(args[1]),
        DateOnly.ParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture));
    Console.WriteLine(JsonSerializer.Serialize(result, PreflightOutputJson.Options));
    return result.Status == "BLOCKED" ? 2 : 0;
}
catch (Exception error)
{
    // Avoid echoing connection strings or arbitrary configuration values.
    Console.Error.WriteLine($"Configuration preflight failed ({error.GetType().Name}). No host was started.");
    return 1;
}

internal static class PreflightOutputJson
{
    internal static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}
