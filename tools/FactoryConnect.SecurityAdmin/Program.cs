using FactoryConnect.Security;

// Intentionally separate from API and normal migration/deployment execution. Credentials
// are never command-line arguments or console output. Server-console authorization is external.
if (args.Length < 2 || args[0] is not ("install" or "bootstrap" or "recover"))
{
    Console.Error.WriteLine("Usage: install <approval-reference> OR bootstrap/recover <approval-reference> <operation-guid> <username> [machine-guid ...]. Secrets via protected environment/interactive console.");
    return 1;
}
var connection = Environment.GetEnvironmentVariable("FC_SECURITY_SQL") ?? throw new InvalidOperationException("Explicit security database required.");
var key = Convert.FromBase64String(Environment.GetEnvironmentVariable("FC_SECURITY_REQUEST_KEY") ?? "");
var authority = new SqlSecurityAuthority(connection, key, TimeProvider.System);
if (args[0] == "install")
{
    await authority.InstallAsync(CancellationToken.None);
    Console.WriteLine("Security schema v1 installed; no user provisioned.");
    return 0;
}
if (args.Length < 4 || Console.IsInputRedirected) throw new InvalidOperationException("Interactive trusted console required.");
Console.Write("Temporary password (hidden): ");
var password = new System.Text.StringBuilder();
while (true)
{
    var input = Console.ReadKey(intercept: true);
    if (input.Key == ConsoleKey.Enter) break;
    if (input.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
    else if (!char.IsControl(input.KeyChar) && password.Length < 256) password.Append(input.KeyChar);
}
Console.WriteLine();
var result = args[0] == "bootstrap"
    ? await authority.BootstrapAsync(Guid.Parse(args[2]), args[3], password.ToString(), args.Skip(4).Select(Guid.Parse).ToArray(), args[1], CancellationToken.None)
    : await authority.RecoverAsync(Guid.Parse(args[2]), args[3], password.ToString(), args[1], CancellationToken.None);
password.Clear();
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
return 0;
