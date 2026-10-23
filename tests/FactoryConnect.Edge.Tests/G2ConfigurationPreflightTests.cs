using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryConnect.ConfigurationPreflight;
using Xunit;

namespace FactoryConnect.Edge.Tests;

[CollectionDefinition("G2 environment composition", DisableParallelization = true)]
public sealed class G2EnvironmentFixtureGroup { }

[Collection("G2 environment composition")]
public sealed class G2ConfigurationPreflightTests
{
    [Fact]
    public void ProjectionMatchesLauncherArraysNullAndBooleanRules()
    {
        using var json = JsonDocument.Parse("""{"A":[{"B":true},null,false,3,"value"],"Empty":[]}""");
        var values = G2ConfigurationPreflight.Project(json.RootElement);
        Assert.Equal("true", values["A__0__B"]);
        Assert.Equal("", values["A__1"]);
        Assert.Equal("false", values["A__2"]);
        Assert.Equal("3", values["A__3"]);
        Assert.Equal("value", values["A__4"]);
        Assert.Equal(5, values.Count);
    }

    [Fact]
    public async Task UnresolvedBusinessInputsBlockBeforeHostBinding()
    {
        using var files = new Fixture();
        File.WriteAllText(files.Candidate, """{"ProductionProcessing":{"EffectiveFrom":"UNRESOLVED:business date"}}""");
        var result = await G2ConfigurationPreflight.ValidateAsync(files.Candidate, files.Root, new DateOnly(2026, 10, 22));
        Assert.Equal("BLOCKED", result.Status);
        Assert.Contains("ProductionProcessing:EffectiveFrom", result.Blockers);
        Assert.Empty(result.Shifts);
    }

    [Fact]
    public async Task ProductionHostBindsProjectedCandidateWithoutStartingWorkers()
    {
        using var files = new Fixture();
        var before = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var result = await G2ConfigurationPreflight.ValidateAsync(files.Candidate, files.Root, new DateOnly(2026, 10, 22));
        Assert.Equal("BOUND_NOT_DEPLOYMENT_APPROVED", result.Status);
        Assert.Equal("India Standard Time", result.TimeZoneId);
        Assert.Equal(24, result.Shifts.Length);
        Assert.Equal(new double[] { 480, 450, 420 }, result.Shifts.Take(3).Select(static shift => shift.PlannedMinutes));
        Assert.Equal(1350, result.Shifts.Take(3).Sum(static shift => shift.PlannedMinutes));
        Assert.Equal(new DateOnly(2026, 10, 22), result.Shifts[2].FactoryDate);
        Assert.Equal(result.Shifts[2].EndsAtUtc, result.Shifts[3].StartsAtUtc);
        Assert.Equal(before, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));
    }

    [Fact]
    public async Task JsonArrayTailCannotSilentlyAddSampleAuthority()
    {
        using var files = new Fixture();
        File.WriteAllText(Path.Combine(files.Root, "appsettings.Production.json"), """
            {"ProductionProcessing":{"Machines":[{},{"MachineId":"sample-tail"}]}}
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => G2ConfigurationPreflight.ValidateAsync(
            files.Candidate, files.Root, new DateOnly(2026, 10, 22)));
    }

    [Fact]
    public async Task MissingContextCannotInheritFromProductionJson()
    {
        using var files = new Fixture();
        var candidate = JsonNode.Parse(File.ReadAllText(files.Candidate))!;
        candidate["ProductionProcessing"]!["Machines"]![0]!.AsObject().Remove("Contexts");
        File.WriteAllText(files.Candidate, candidate.ToJsonString());
        File.WriteAllText(Path.Combine(files.Root, "appsettings.Production.json"), """
            {"ProductionProcessing":{"Machines":[{"Contexts":[{"AssignmentId":"sample-context","EffectiveFrom":"2026-01-01T00:00:00Z"}]}]}}
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => G2ConfigurationPreflight.ValidateAsync(
            files.Candidate, files.Root, new DateOnly(2026, 10, 22)));
    }

    [Fact]
    public async Task UnprojectedInheritedAssignmentIsDetectedAndEnvironmentRestored()
    {
        using var files = new Fixture();
        const string key = "ProductionProcessing__Machines__0__Contexts__1__AssignmentId";
        var before = Environment.GetEnvironmentVariable(key);
        var environmentBefore = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable(key, "inherited-context");
            await Assert.ThrowsAsync<InvalidOperationException>(() => G2ConfigurationPreflight.ValidateAsync(
                files.Candidate, files.Root, new DateOnly(2026, 10, 22)));
            Assert.Equal("inherited-context", Environment.GetEnvironmentVariable(key));
            Assert.Equal(environmentBefore, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));
        }
        finally { Environment.SetEnvironmentVariable(key, before); }
    }

    [Fact]
    public async Task CandidateEnvironmentOverridesProductionJsonScalarValues()
    {
        using var files = new Fixture();
        File.WriteAllText(Path.Combine(files.Root, "appsettings.Production.json"), """
            {"ProductionProcessing":{"ShiftSchedules":[{"StartsAtLocal":"06:00:00"}]}}
            """);
        var result = await G2ConfigurationPreflight.ValidateAsync(files.Candidate, files.Root, new DateOnly(2026, 10, 22));
        Assert.Equal("BOUND_NOT_DEPLOYMENT_APPROVED", result.Status);
        Assert.Equal(510, result.Shifts[0].GrossMinutes);
    }

    [Fact]
    public async Task MissingPlannedAssignmentCannotInheritFromJson()
    {
        using var files = new Fixture();
        var candidate = JsonNode.Parse(File.ReadAllText(files.Candidate))!;
        candidate["ProductionProcessing"]!["Machines"]![0]!.AsObject().Remove("PlannedProduction");
        File.WriteAllText(files.Candidate, candidate.ToJsonString());
        File.WriteAllText(Path.Combine(files.Root, "appsettings.Production.json"), """
            {"ProductionProcessing":{"Machines":[{"PlannedProduction":{"AssignmentId":"sample-planned"}}]}}
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => G2ConfigurationPreflight.ValidateAsync(
            files.Candidate, files.Root, new DateOnly(2026, 10, 22)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fc-g2-" + Guid.NewGuid().ToString("N"));
        public string Candidate => Path.Combine(Root, "candidate.json");

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "appsettings.json"), """
                {"ProductionProcessing":{"BatchSize":"100","PollingInterval":"00:00:01"}}
                """);
            // Synthetic contract fixture, never a Gajra-approved effective date or policy.
            File.WriteAllText(Candidate, """
            {
              "MTConnect":{"Machines":[{"BaseUri":"http://localhost:1","MachineId":"677b030b-1ede-4a67-82ae-6a494791357f","DeviceKey":"GG-003","FromSequence":"1","PollingInterval":"00:00:01"}]},
              "ProductionProcessing":{
                "Machines":[{
                  "MachineId":"677b030b-1ede-4a67-82ae-6a494791357f","ActivityStreamKey":"mtconnect:GG-003",
                  "CompanyId":"COMP","SiteId":"SITE","ProductionLineId":"LINE",
                  "Contexts":[{"AssignmentId":"fixture-context","EffectiveFrom":"2026-10-22T07:30:00+05:30"}],
                  "PlannedProduction":{
                    "AssignmentId":"fixture-planned","TimeZoneId":"India Standard Time","EffectiveFrom":"2026-10-22",
                    "PlannedWindows":[
                      {"StartsAtLocal":"07:30:00","EndsAtLocal":"16:00:00","StartDayOffset":0},
                      {"StartsAtLocal":"16:00:00","EndsAtLocal":"00:00:00","StartDayOffset":0},
                      {"StartsAtLocal":"00:00:00","EndsAtLocal":"07:30:00","StartDayOffset":1}],
                    "BreakWindows":[
                      {"StartsAtLocal":"12:00:00","EndsAtLocal":"12:30:00","StartDayOffset":0},
                      {"StartsAtLocal":"20:00:00","EndsAtLocal":"20:30:00","StartDayOffset":0},
                      {"StartsAtLocal":"03:30:00","EndsAtLocal":"04:00:00","StartDayOffset":1}]
                  }
                }],
                "ShiftSchedules":[
                  {"AssignmentId":"fixture-shift-1","CompanyId":"COMP","SiteId":"SITE","ProductionLineId":"LINE","ShiftId":"SHIFT-1","Name":"Shift 1","TimeZoneId":"India Standard Time","StartsAtLocal":"07:30:00","EndsAtLocal":"16:00:00","StartDayOffset":0,"EffectiveFrom":"2026-10-22","ActiveDays":["Sunday","Monday","Tuesday","Wednesday","Thursday","Friday","Saturday"]},
                  {"AssignmentId":"fixture-shift-2","CompanyId":"COMP","SiteId":"SITE","ProductionLineId":"LINE","ShiftId":"SHIFT-2","Name":"Shift 2","TimeZoneId":"India Standard Time","StartsAtLocal":"16:00:00","EndsAtLocal":"00:00:00","StartDayOffset":0,"EffectiveFrom":"2026-10-22","ActiveDays":["Sunday","Monday","Tuesday","Wednesday","Thursday","Friday","Saturday"]},
                  {"AssignmentId":"fixture-shift-3","CompanyId":"COMP","SiteId":"SITE","ProductionLineId":"LINE","ShiftId":"SHIFT-3","Name":"Shift 3","TimeZoneId":"India Standard Time","StartsAtLocal":"00:00:00","EndsAtLocal":"07:30:00","StartDayOffset":1,"EffectiveFrom":"2026-10-22","ActiveDays":["Sunday","Monday","Tuesday","Wednesday","Thursday","Friday","Saturday"]}
                ]
              }
            }
            """);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
