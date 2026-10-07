using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.HistoricalRecovery;
using FactoryConnect.Persistence.SqlServer;

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("Preview: historical-recovery <2026-10-04|2026-10-05>");
    Console.WriteLine("Apply: historical-recovery <day> --apply <preview.json> --deployment-root <root> --approved-release <SHA> --approved-manifest-sha256 <SHA256>");
    Console.WriteLine("Connection: FACTORYCONNECT_RECOVERY_CONNECTION_STRING. JSON to stdout; credentials never arguments/output.");
    Console.WriteLine("Approved release/hash require independent package/source review retaining P0-C. Factory preview/apply require separate authorization.");
    return 0;
}
try
{
    if (args.Length is not (1 or 9)) throw new ArgumentException("Invalid arguments; use --help.");
    var target = RecoveryTargets.ForDate(args[0]);
    var apply = args.Length == 9;
    if (apply && (args[1] != "--apply" || args[3] != "--deployment-root" || args[5] != "--approved-release" || args[7] != "--approved-manifest-sha256"))
        throw new ArgumentException("Invalid apply flags; use --help.");
    var connection = Environment.GetEnvironmentVariable("FACTORYCONNECT_RECOVERY_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Connection environment variable absent.");
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    using var reader = new SqlHistoricalRecoveryReader(connection);
    var runner = new HistoricalRecoveryRunner(reader, reader,
        apply ? new SqlServerOperationalMetricProjectionRecoveryStore(connection) : null,
        apply ? new PowerShellDeploymentVerifier(args[4], args[6], args[8]) : null);
    if (!apply)
    {
        var record = await runner.PreviewAsync(target, cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(record, RecoveryJson.Options));
        return 0;
    }
    using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(args[2], cancellation.Token));
    var applied = await runner.ApplyAsync(target, bundle.RootElement, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(applied, RecoveryJson.Options));
    return applied.Result.Outcome == FactoryConnect.Abstractions.OperationalMetricProjectionRecoveryOutcome.Conflict ? 3 : 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Recovery command cancelled. If apply was attempted, inspect retained target before retry."); return 2; }
catch (Exception e)
{
    Console.Error.WriteLine($"Recovery command failed ({e.GetType().Name}). If apply was attempted, commit outcome may require verification. No credentials emitted.");
    return 1;
}

internal sealed class PowerShellDeploymentVerifier(string root, string release, string manifest) : IRecoveryDeploymentVerifier
{
    public async Task<JsonElement> VerifyAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Factory apply requires Windows deployment verification.");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Test-FactoryConnectRecoveryDeployment.ps1"),
            "-InstallRoot", root, "-ApprovedRelease", release, "-ApprovedManifestSha256", manifest }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Deployment verifier could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout; _ = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Deployment verification failed.");
        using var evidence = JsonDocument.Parse(output);
        if (evidence.RootElement.GetProperty("Status").GetString() != "Verified") throw new InvalidDataException("Deployment verification not obtained.");
        return evidence.RootElement.Clone();
    }
}
