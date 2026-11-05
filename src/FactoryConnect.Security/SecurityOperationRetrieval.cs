using System.Text.Json;
using Microsoft.Data.SqlClient;
namespace FactoryConnect.Security;

public sealed partial class SqlSecurityAuthority
{
    // Self-result lookup only. Separate action grant; registration ownership alone grants nothing.
    // No token argument, request bytes, credential fingerprint, or reset delivery secret is returned.
    public Task<HistoricalSecurityResult> ReadPasswordResetResultAsync(string secret, Guid operationId, CancellationToken token) =>
        TransactionAsync("Shared", async ctx =>
        {
            var identity = Require(ctx.State, secret, SecurityActions.RetrieveResetResult, null);
            if (operationId == Guid.Empty) throw new ArgumentException("Operation ID required.", nameof(operationId));
            await using var command = new SqlCommand("SELECT Request,Result FROM dbo.FactoryConnectSecurityOperation WHERE SubjectId=@Subject AND OperationId=@Operation;", ctx.Connection, ctx.Transaction);
            command.Parameters.AddWithValue("@Subject", identity.SubjectId); command.Parameters.AddWithValue("@Operation", operationId);
            HistoricalSecurityResult result;
            await using (var rows = await command.ExecuteReaderAsync(token))
            {
                if (!await rows.ReadAsync(token)) result = new("NotFound");
                else
                {
                    var text = System.Text.Encoding.UTF8.GetString((byte[])rows[0]);
                    const string prefix = "factoryconnect-security-command:v1\n";
                    if (!text.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unrecognized operation encoding.");
                    var request = JsonSerializer.Deserialize<SecurityCommand>(text[prefix.Length..]) ?? throw new InvalidDataException("Invalid operation request.");
                    if (request.Action != "password.reset.consume" || request.OperationId != operationId) result = new("NotFound");
                    else result = rows.IsDBNull(1) ? new("Pending") : new("Completed",
                        (JsonSerializer.Deserialize<SecurityResult>(rows.GetString(1)) ?? throw new InvalidDataException("Invalid recorded result.")) with { Historical = true });
                }
            }
            await ctx.AuditAsync(identity.SubjectId, identity.SessionId, operationId, SecurityActions.RetrieveResetResult, result.Status, token);
            return result;
        }, token, save: false, phase: "reset.result");
}
