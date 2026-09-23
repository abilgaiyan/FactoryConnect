using FactoryConnect.Rehearsal.MTConnectFixture;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var state = new RehearsalMtConnectState();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Rehearsal.MTConnectFixture",
    machines = RehearsalFixtureTopology.Machines.Count,
    instanceId = RehearsalFixtureTopology.InstanceId
}));

foreach (var machine in RehearsalFixtureTopology.Machines)
{
    var selected = machine;
    var basePath = selected.BasePath.TrimEnd('/');

    app.MapGet(basePath + "/probe", () =>
        Results.Text(
            state.GetProbeDocument(selected),
            "application/xml"));

    app.MapGet(basePath + "/current", () =>
        Results.Text(
            state.GetCurrentDocument(selected),
            "application/xml"));

    app.MapGet(basePath + "/sample", (HttpRequest request) =>
    {
        if (!ulong.TryParse(
                request.Query["from"],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var fromSequence))
        {
            return Results.BadRequest();
        }

        return Results.Text(
            state.GetSampleDocument(selected, fromSequence),
            "application/xml");
    });
}

app.Run();

public partial class Program;
