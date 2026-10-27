using FactoryConnect.Persistence.SqlServer;

// Credentials are inherited privately, never accepted on a process command line.
if (args.Length != 5) throw new ArgumentException("Expected operation ID, server, database, approval and maintenance evidence path.");
var connectionString = Environment.GetEnvironmentVariable("FACTORYCONNECT_MOXA_RECOVERY_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Recovery connection string is missing.");
await MoxaAdmissionRecovery.ExecuteAsync(connectionString, Guid.Parse(args[0]), args[1], args[2], args[3],
    await File.ReadAllTextAsync(args[4]), CancellationToken.None);
Console.WriteLine("MOXA admission correction and audit committed. Production acceptance remains pending.");
