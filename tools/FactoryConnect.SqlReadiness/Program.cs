using System.Text.Json;
using Microsoft.Data.SqlClient;

var stage = "Deserialize";
// Secrets enter only through redirected stdin. Never print provider diagnostics.
try
{
    var request = JsonSerializer.Deserialize<Request>(await Console.In.ReadToEndAsync());
    if (request is null || request.BudgetMilliseconds is < 1 or > 30000)
        return Reply("ConfigurationInvalid", 10);
    stage = "ConnectionString";
    var builder = new SqlConnectionStringBuilder(request.ConnectionString);
    if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
        return Reply("ConfigurationInvalid", 10);
    stage = "Operation";
    if (request.Operation == "Validate")
        return Reply("Valid", 0);
    if (request.Operation != "Probe")
        return Reply("ConfigurationInvalid", 10);
    builder.ConnectTimeout = Math.Max(1, (int)Math.Ceiling(request.BudgetMilliseconds / 1000.0));
    builder.Pooling = false;
    using var deadline = new CancellationTokenSource(request.BudgetMilliseconds);
    await using var connection = new SqlConnection(builder.ConnectionString);
    try
    {
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = builder.ConnectTimeout;
        var result = await command.ExecuteScalarAsync(deadline.Token);
        return result is int value && value == 1 ? Reply("Ready", 0) : Reply("Unavailable", 11);
    }
    catch (SqlException exception) when (exception.Number == 18456)
    {
        return Reply("AuthenticationRejected", 12);
    }
    catch (Exception)
    {
        return Reply("Unavailable", 11);
    }
}
catch (Exception)
{
    Console.Error.WriteLine(stage);
    return Reply("ConfigurationInvalid", 10);
}

static int Reply(string status, int exitCode)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Status = status }));
    return exitCode;
}

internal sealed record Request(string Operation, string ConnectionString, int BudgetMilliseconds);
