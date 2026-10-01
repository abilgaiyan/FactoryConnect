using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
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

    if (builder.Configuration.GetValue<bool>("DemoStandardProvisioning:Enabled"))
    {
        var connectionString = options.ConnectionString ?? string.Empty;
        var database = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!string.Equals(database, "FactoryConnect_Demo", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Demo standard provisioning requires FactoryConnect_Demo.");
        }

        var authority = new SqlServerProductionStandardAuthority(connectionString);
        await authority.PublishAsync(new ProductionStandardVersion
        {
            VersionId = "factoryconnect-demo-part-operation-v1",
            CompanyId = new CompanyId("GAJRA"),
            SiteId = new SiteId("GAJRA-NEW"),
            PartId = new PartId("DEMO-PART"),
            OperationId = new OperationId("DEMO-OPERATION"),
            SecondsPerUnit = 0.5m,
            EffectiveFromUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            SourceReference = "seven-machine-synthetic-demo",
            PublishedRevision = 1,
        }, cancellationSource.Token);
    }

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
