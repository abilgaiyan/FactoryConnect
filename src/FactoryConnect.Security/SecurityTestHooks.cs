using Microsoft.Data.SqlClient;
namespace FactoryConnect.Security;
// Internal, friend-test-only coordination/fault injection. Never configuration/HTTP accessible.
internal sealed class SecurityTestHooks
{
    internal Func<CancellationToken, Task>? BeforeCredentialVerification { get; init; }
    internal Func<SqlConnection, SqlTransaction, string, CancellationToken, Task>? BeforeGate { get; init; }
    internal Func<SqlConnection, SqlTransaction, string, CancellationToken, Task>? AfterGate { get; init; }
    internal Func<SqlConnection, SqlTransaction, string, CancellationToken, Task>? BeforeCommit { get; init; }
    internal Func<string, CancellationToken, Task>? AfterCommit { get; init; }
    internal Func<CancellationToken, Task>? BeforeDenialAudit { get; init; }
}
public sealed class SecurityDenialAuditException : UnauthorizedAccessException
{
    public SecurityDenialAuditException(Exception denial, Exception auditFailure)
        : base("Authorization denied; denial audit could not be persisted.", new AggregateException(denial, auditFailure)) { }
}
