using System.Globalization;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FactoryConnect.ConfigurationPreflight;

public sealed record G2ShiftResult(string ShiftId, string AssignmentId, DateOnly FactoryDate,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, double GrossMinutes, double PlannedMinutes);

public sealed record G2PreflightResult(string Status, string[] Blockers, string? MachineId,
    string? TimeZoneId, string? CompanyId, string? SiteId, string? ProductionLineId,
    string[] ContextAssignmentIds, string[] PlannedAssignmentIds, G2ShiftResult[] Shifts);

public static class G2ConfigurationPreflight
{
    // This helper changes process environment temporarily. Call only from the dedicated
    // preflight process, or a nonparallel test collection. No host is built or started.
    public static async Task<G2PreflightResult> ValidateAsync(
        string candidatePath, string releaseDirectory, DateOnly businessDate)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(candidatePath));
        var projected = Project(document.RootElement);
        var blockers = projected.Where(static item =>
                item.Value.StartsWith("UNRESOLVED:", StringComparison.Ordinal))
            .Select(static item => item.Key.Replace("__", ":", StringComparison.Ordinal)).ToArray();
        if (blockers.Length != 0)
        {
            return new G2PreflightResult("BLOCKED", blockers, null, null, null, null, null, [], [], []);
        }
        if (!File.Exists(Path.Combine(releaseDirectory, "appsettings.json")))
            throw new InvalidOperationException("An actual Edge content root is required.");

        // Preserve inherited environment exactly as the launcher does, overriding only
        // projected keys and the two explicit Production selectors, then restore it.
        projected["DOTNET_ENVIRONMENT"] = "Production";
        projected["ASPNETCORE_ENVIRONMENT"] = "Production";
        var saved = projected.Keys.ToDictionary(static key => key,
            static key => Environment.GetEnvironmentVariable(key), StringComparer.OrdinalIgnoreCase);
        IConfigurationRoot? configuration = null;
        try
        {
            foreach (var item in projected) Environment.SetEnvironmentVariable(item.Key, item.Value);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = releaseDirectory,
                ApplicationName = typeof(EdgeApplicationServiceCollectionExtensions).Assembly.GetName().Name,
                Args = [],
            });
            if (!builder.Environment.IsProduction()) throw new InvalidOperationException("Production host required.");
            configuration = builder.Configuration;
            RequireExplicitAssignments(configuration, projected);
            if (configuration.GetValue<bool>("DemoCanonicalInputs:Enabled"))
                throw new InvalidOperationException("Demo quantity inputs cannot be enabled.");

            var inventory = MtConnectMachineInventory.FromConfiguration(configuration);
            if (inventory.MachineIds.Count != 1) throw new InvalidOperationException("GG-003 requires one machine.");
            builder.Services.AddFactoryConnectProductionMetricInputs(configuration, inventory.ActivityStreams);
            // Deliberately bind only production metric inputs. Do not register persistence,
            // acquisition, startup readiness, or invoke any IHostedService.
            await using var provider = builder.Services.BuildServiceProvider();
            var machine = configuration.GetRequiredSection("ProductionProcessing:Machines:0");
            var site = new SiteId(machine["SiteId"]!);
            var line = new ProductionLineId(machine["ProductionLineId"]!);
            var shifts = await provider.GetRequiredService<FactoryConnect.Core.ShiftOccurrenceResolver>()
                .ResolveAsync(site, line, businessDate, businessDate.AddDays(8), CancellationToken.None);
            var planned = await provider.GetRequiredService<FactoryConnect.Core.PlannedProductionIntervalResolver>()
                .ResolveAsync(site, line, businessDate, businessDate.AddDays(8), CancellationToken.None);
            var ordered = shifts.OrderBy(static shift => shift.StartsAtUtc).ToArray();
            if (ordered.Length != 24) throw new InvalidOperationException("Eight complete business dates required.");
            for (var index = 1; index < ordered.Length; index++)
                if (ordered[index - 1].EndsAtUtc != ordered[index].StartsAtUtc)
                    throw new InvalidOperationException("Shift coverage has a gap or collision.");

            var timeZoneId = configuration["ProductionProcessing:ShiftSchedules:0:TimeZoneId"]!;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var expectedLocalStart = businessDate.ToDateTime(new TimeOnly(7, 30));
            if (TimeZoneInfo.ConvertTime(ordered[0].StartsAtUtc, zone).DateTime != expectedLocalStart)
                throw new InvalidOperationException("Business day must start at 07:30.");
            var contextReader = provider.GetRequiredService<IProductionContextReader>();
            var contexts = await contextReader.ReadAsync(inventory.MachineIds[0],
                ordered[0].StartsAtUtc, ordered[^1].EndsAtUtc, CancellationToken.None);
            var contextCursor = ordered[0].StartsAtUtc;
            foreach (var context in contexts.OrderBy(static item => item.EffectiveFrom))
            {
                if (context.EffectiveFrom > contextCursor) throw new InvalidOperationException("Context coverage gap.");
                if (context.EffectiveTo is null) { contextCursor = ordered[^1].EndsAtUtc; break; }
                if (context.EffectiveTo.Value > contextCursor) contextCursor = context.EffectiveTo.Value;
            }
            if (contextCursor < ordered[^1].EndsAtUtc) throw new InvalidOperationException("Context coverage incomplete.");

            foreach (var interval in planned)
                if (!ordered.Any(shift => shift.FactoryDate == interval.FactoryDate &&
                    interval.StartsAtUtc >= shift.StartsAtUtc && interval.EndsAtUtc <= shift.EndsAtUtc))
                    throw new InvalidOperationException("Planned interval lacks a unique shift owner.");
            foreach (var day in ordered.GroupBy(static shift => shift.FactoryDate))
            {
                var dayShifts = day.OrderBy(static shift => shift.StartsAtUtc).ToArray();
                if (dayShifts.Length != 3 ||
                    !dayShifts.Select(static shift => shift.ShiftId.ToString())
                        .SequenceEqual(new[] { "SHIFT-1", "SHIFT-2", "SHIFT-3" }) ||
                    !dayShifts.Select(static shift => shift.Duration.TotalMinutes)
                        .SequenceEqual(new double[] { 510, 480, 450 }))
                    throw new InvalidOperationException("Expected GG-003 three-shift gross schedule required.");
            }
            if (planned.Count == 0) throw new InvalidOperationException("Planned production coverage is empty.");
            var results = ordered.Select(shift => new G2ShiftResult(
                shift.ShiftId.ToString(), shift.SourceAssignmentId.ToString(), shift.FactoryDate,
                shift.StartsAtUtc, shift.EndsAtUtc, shift.Duration.TotalMinutes,
                planned.Where(interval => interval.FactoryDate == shift.FactoryDate)
                    .Sum(interval => Math.Max(0, (Min(interval.EndsAtUtc, shift.EndsAtUtc) -
                        Max(interval.StartsAtUtc, shift.StartsAtUtc)).TotalMinutes)))).ToArray();
            return new G2PreflightResult("BOUND_NOT_DEPLOYMENT_APPROVED", [], machine["MachineId"],
                timeZoneId, machine["CompanyId"], machine["SiteId"], machine["ProductionLineId"],
                contexts.Select(static item => item.Id.ToString()).ToArray(),
                planned.Select(static item => item.SourceAssignmentId.ToString()).Distinct().ToArray(), results);
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
            foreach (var item in saved) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static void RequireExplicitAssignments(IConfiguration configuration, Dictionary<string, string> projected)
    {
        // Compare complete subtrees, including array tails left by JSON/environment layering.
        foreach (var prefix in new[] { "ProductionProcessing__Machines", "ProductionProcessing__ShiftSchedules" })
        {
            var expected = projected.Where(item => item.Key.StartsWith(prefix + "__", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.Key.Replace("__", ":", StringComparison.Ordinal),
                    static item => item.Value, StringComparer.OrdinalIgnoreCase);
            var actual = configuration.GetSection(prefix.Replace("__", ":", StringComparison.Ordinal))
                .AsEnumerable().Where(static item => item.Value is not null)
                .ToDictionary(static item => item.Key, static item => item.Value!, StringComparer.OrdinalIgnoreCase);
            if (expected.Count == 0 || expected.Count != actual.Count ||
                expected.Any(item => !actual.TryGetValue(item.Key, out var value) || value != item.Value))
                throw new InvalidOperationException("Effective assignment configuration differs from explicit candidate.");
        }
    }

    public static Dictionary<string, string> Project(JsonElement value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Visit(value, "", result);
        return result;
    }

    private static void Visit(JsonElement value, string prefix, Dictionary<string, string> output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    Visit(property.Value, prefix.Length == 0 ? property.Name : prefix + "__" + property.Name, output);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var child in value.EnumerateArray())
                    Visit(child, prefix + "__" + (index++).ToString(CultureInfo.InvariantCulture), output);
                break;
            default:
                if (prefix.Length == 0) throw new InvalidOperationException("Configuration root must be an object.");
                output.Add(prefix, value.ValueKind switch
                {
                    JsonValueKind.Null => "", JsonValueKind.String => value.GetString()!,
                    JsonValueKind.True => "true", JsonValueKind.False => "false", _ => value.GetRawText(),
                });
                break;
        }
    }
}
