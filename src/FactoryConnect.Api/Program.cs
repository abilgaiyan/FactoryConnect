var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "FactoryConnect.Api"
}));

app.MapOpenApi();

app.Run();

public partial class Program;
