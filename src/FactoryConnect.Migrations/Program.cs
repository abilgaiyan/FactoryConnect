using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var options = new SqlServerPersistenceOptions();
builder.Configuration
    .GetSection(SqlServerPersistenceOptions.SectionName)
    .Bind(options);

using var cancellationSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

Console.CancelKeyPress += cancelHandler;

try
{
    await SqlServerMigrationOperation.ApplyAsync(
        options.ConnectionString ?? string.Empty,
        options.Startup?.LockTimeout ?? TimeSpan.FromSeconds(30),
        cancellationSource.Token);

    Console.WriteLine("FactoryConnect SQL migration completed successfully. Database schema is repository-current.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("FactoryConnect SQL migration was canceled.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FactoryConnect SQL migration failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
