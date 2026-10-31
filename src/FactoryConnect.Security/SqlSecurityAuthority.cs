using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Security;

// Pilot-wide database/principal-scoped gate. Authorization writers take Exclusive;
// protected work takes Shared. No upgrades. Row ownership never leaves this transaction.
public sealed partial class SqlSecurityAuthority(string connectionString, byte[] requestKey, TimeProvider clock)
{
    private readonly PasswordHasher<LocalAccount> _hasher = new();
    private readonly string _dummyHash = new PasswordHasher<LocalAccount>().HashPassword(new LocalAccount(), SecurityRules.NewToken());
    private readonly byte[] _requestKey = requestKey.Length >= 32 ? requestKey.ToArray() : throw new ArgumentException("A persistent request key of at least 32 bytes is required.", nameof(requestKey));
    public const string Gate = "FactoryConnect.Security.Eligibility.v1";

    public async Task InstallAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockAsync(connection, transaction, Gate, "Exclusive", token);
        using var resource = typeof(SqlSecurityAuthority).Assembly.GetManifestResourceStream("FactoryConnect.Security.security-v1.sql")!;
        using var reader = new StreamReader(resource);
        await using var command = new SqlCommand(await reader.ReadToEndAsync(token), connection, transaction);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "INSERT dbo.FactoryConnectSecurityState VALUES (1,1,0,@Payload);";
        command.Parameters.AddWithValue("@Payload", JsonSerializer.Serialize(new SecurityState()));
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    // Only the installation tool calls these. No HTTP mapping, default credential or auto-seed.
    public Task<SecurityResult> BootstrapAsync(Guid operation, string username, string password, Guid[] machines, string approval, CancellationToken token) =>
        OfflineAsync(operation, "bootstrap", username, password, machines, approval, token);
    public Task<SecurityResult> RecoverAsync(Guid operation, string username, string password, string approval, CancellationToken token) =>
        OfflineAsync(operation, "recover", username, password, [], approval, token);

    private async Task<SecurityResult> OfflineAsync(Guid operation, string action, string username, string password, Guid[] machines, string approval, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approval);
        if (approval.Length > 256 || operation == Guid.Empty || machines.Any(id => id == Guid.Empty)) throw new ArgumentException("Invalid recovery envelope.");
        SecurityRules.ValidatePassword(password);
        var normalized = SecurityRules.NormalizeName(username);
        var envelope = new SecurityCommand(operation, 0, action, null, null, username, password, null, machines, null);
        // Approval evidence participates in content matching without storing potentially private text.
        var bytes = SecurityRules.CanonicalRequest(envelope with { Name = username + "\n" + approval }, _requestKey);
        await TransactionAsync("Exclusive", async ctx =>
        {
            await RegisterAsync(ctx, Guid.Empty, operation, bytes, token);
            return true;
        }, token);
        return await TransactionAsync("Exclusive", async ctx =>
        {
            var previous = await ResultAsync(ctx, Guid.Empty, operation, bytes, token);
            if (previous is not null) return previous with { Historical = true };
            SecurityResult result;
            if (action == "bootstrap")
            {
                if (ctx.State.Accounts.Count != 0) result = new("BootstrapAlreadyCompleted");
                else
                {
                    var account = NewAccount(username, normalized, password);
                    account.Id = await AllocatedIdAsync(ctx, Guid.Empty, operation, token);
                    var role = new SecurityRole(Guid.NewGuid(), "FactoryConnect Administrator", [
                        .. SecurityActions.Catalogue.Where(a => a != SecurityActions.ViewReports).Select(a => new AccessGrant(a, null, true)),
                        .. machines.Distinct().Select(id => new AccessGrant(SecurityActions.ViewReports, id, true))]);
                    account.Roles.Add(role.Id); ctx.State.Roles.Add(role); ctx.State.Accounts.Add(account);
                    result = new("Created", account.Id, ctx.Revision + 1);
                }
            }
            else
            {
                var account = ctx.State.Accounts.SingleOrDefault(a => a.NormalizedUserName == normalized);
                if (account is null) result = new("RecoveryTargetUnavailable");
                else
                {
                    account.PasswordHash = _hasher.HashPassword(account, password);
                    account.SecurityStamp = Guid.NewGuid().ToString("N");
                    account.Disabled = false; account.MustChangePassword = true; account.TemporaryCredentialExpiresAt = clock.GetUtcNow() + SecurityRules.ResetLifetime; account.AccessFailedCount = 0; account.LockoutEnd = null;
                    Revoke(ctx.State, account.Id);
                    result = new("Recovered", account.Id, ctx.Revision + 1);
                }
            }
            await CompleteAsync(ctx, Guid.Empty, null, operation, action, result, token);
            return result;
        }, token);
    }

    public async Task<(string? Token, string Outcome)> LoginAsync(string username, string password, CancellationToken token)
    {
        if (username.Length > 128 || password.Length > 256) return (null, "InvalidCredentials");
        var normalized = username.ToUpperInvariant();
        return await TransactionAsync("Exclusive", async ctx =>
        {
            var now = clock.GetUtcNow();
            // Bounded global rate limit includes unknown names; no unbounded attacker-controlled rows.
            var window = ctx.State.LoginWindows.GetValueOrDefault("login");
            if (window is null || now >= window.StartedAt.AddMinutes(1)) window = new(now, 0);
            if (window.Attempts >= 30)
            {
                await ctx.AuditAsync(null, null, null, "login", "TryLater", token);
                return ((string?)null, "TryLater");
            }
            ctx.State.LoginWindows["login"] = window with { Attempts = window.Attempts + 1 };
            var account = ctx.State.Accounts.SingleOrDefault(a => a.NormalizedUserName == normalized);
            var permitted = account is not null && !account.Disabled && (!account.MustChangePassword || account.TemporaryCredentialExpiresAt is { } expiry && now < expiry) && (account.LockoutEnd is null || now >= account.LockoutEnd);
            var check = _hasher.VerifyHashedPassword(account ?? new LocalAccount(), permitted ? account!.PasswordHash! : _dummyHash, password);
            if (!permitted) check = PasswordVerificationResult.Failed;
            if (check == PasswordVerificationResult.Failed)
            {
                if (permitted)
                {
                    account!.AccessFailedCount++;
                    if (account.AccessFailedCount >= 5) { account.LockoutEnd = now.AddMinutes(15); account.AccessFailedCount = 0; }
                }
                await ctx.AuditAsync(account?.Id, null, null, "login", "InvalidCredentials", token);
                return ((string?)null, "InvalidCredentials");
            }
            if (check == PasswordVerificationResult.SuccessRehashNeeded) account!.PasswordHash = _hasher.HashPassword(account, password);
            account!.AccessFailedCount = 0; account.LockoutEnd = null;
            var secret = SecurityRules.NewToken();
            ctx.State.Sessions.Add(new(Guid.NewGuid(), account.Id, SecurityRules.TokenHash(secret), now, now, false));
            await ctx.AuditAsync(account.Id, ctx.State.Sessions[^1].Id, null, "login", "Succeeded", token);
            return ((string?)secret, "Succeeded");
        }, token);
    }

    public Task<AuthenticatedSession?> AuthenticateAsync(string secret, CancellationToken token) =>
        TransactionAsync("Shared", ctx => Task.FromResult(Resolve(ctx.State, secret)), token, save: false);

    // Runs source validation and business work in the SAME transaction after authorization.
    // Consumers must acquire their operation/thread/source locks after this gate; no network I/O.
    public Task<T> ExecuteAuthorizedAsync<T>(string secret, string action, Guid? machine,
        Func<SqlConnection, SqlTransaction, AuthenticatedSession, CancellationToken, Task<T>> work, CancellationToken token) =>
        TransactionAsync("Shared", async ctx =>
        {
            var identity = Require(ctx.State, secret, action, machine);
            var result = await work(ctx.Connection, ctx.Transaction, identity, token);
            await ctx.AuditAsync(identity.SubjectId, identity.SessionId, null, action, "Authorized", token, machine);
            return result;
        }, token, save: false);

    public async Task<SecurityResult> ExecuteAsync(string secret, SecurityCommand command, CancellationToken token)
    {
        if (command.OperationId == Guid.Empty || command.ExpectedRevision < 0) throw new ArgumentException("Invalid operation envelope.", nameof(command));
        if (command.MachineId is not null) throw new ArgumentException("Machine scope is carried by exact grants, not an alternate command scope.", nameof(command));
        var permission = Permission(command.Action);
        var bytes = SecurityRules.CanonicalRequest(command, _requestKey);
        // Registration survives execution rollback. Auth runs before inspecting operation/target.
        var subject = await TransactionAsync("Exclusive", async ctx =>
        {
            var identity = RequireCommand(ctx.State, secret, command, permission);
            await RegisterAsync(ctx, identity.SubjectId, command.OperationId, bytes, token);
            return identity.SubjectId;
        }, token, save: false);
        return await TransactionAsync("Exclusive", async ctx =>
        {
            var identity = RequireCommand(ctx.State, secret, command, permission);
            if (identity.SubjectId != subject) throw new UnauthorizedAccessException();
            var old = await ResultAsync(ctx, subject, command.OperationId, bytes, token);
            if (old is not null) return old with { Historical = true };
            SecurityResult result;
            if (ctx.Revision != command.ExpectedRevision) result = new("StaleRevision");
            else
            {
                // Do not catch infrastructure or unexpected failures as business rejections.
                try { result = Apply(ctx.State, identity, command, ctx.Revision + 1, await AllocatedIdAsync(ctx, subject, command.OperationId, token)); }
                catch (ArgumentException) { result = new("InvalidRequest"); }
            }
            await CompleteAsync(ctx, subject, identity.SessionId, command.OperationId, command.Action, result, token);
            return result;
        }, token);
    }

    public Task<SecurityResult> SessionActionAsync(string secret, string action, Guid operation, CancellationToken token) =>
        TransactionAsync("Shared", async ctx =>
        {
            var identity = Resolve(ctx.State, secret) ?? throw new UnauthorizedAccessException();
            if (operation == Guid.Empty || action is not ("logout" or "interactive")) throw new ArgumentException("Invalid session action.");
            var bytes = System.Text.Encoding.UTF8.GetBytes($"session:v1:{identity.SessionId:D}:{action}");
            await RegisterAsync(ctx, identity.SubjectId, operation, bytes, token);
            var previous = await ResultAsync(ctx, identity.SubjectId, operation, bytes, token);
            if (previous is not null) return previous with { Historical = true };
            var index = ctx.State.Sessions.FindIndex(s => s.Id == identity.SessionId);
            var session = ctx.State.Sessions[index];
            ctx.State.Sessions[index] = action == "logout" ? session with { Revoked = true } : session with { LastInteractiveAt = clock.GetUtcNow() };
            var result = new SecurityResult("Succeeded", identity.SessionId);
            await CompleteAsync(ctx, identity.SubjectId, identity.SessionId, operation, action, result, token);
            return result;
        }, token);

    public Task<object> ReadSessionAsync(string secret, CancellationToken token) =>
        TransactionAsync("Shared", ctx =>
        {
            var identity = Resolve(ctx.State, secret) ?? throw new UnauthorizedAccessException();
            return Task.FromResult<object>(new { identity.SubjectId, identity.SessionId, identity.MustChangePassword, Revision = ctx.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }, token, save: false);

    public Task<object> ReadAuditAsync(string secret, long after, CancellationToken token) =>
        ExecuteAuthorizedAsync<object>(secret, SecurityActions.ViewAudit, null, async (connection, transaction, identity, ct) =>
        {
            await using var cmd = new SqlCommand("SELECT TOP (100) Sequence,OccurredAt,SubjectId,SessionId,OperationId,Action,Outcome FROM dbo.FactoryConnectSecurityAudit WHERE Sequence>@After ORDER BY Sequence;", connection, transaction);
            cmd.Parameters.AddWithValue("@After", after);
            var items = new List<object>();
            await using var rows = await cmd.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) items.Add(new { Sequence = rows.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture), OccurredAt = rows.GetDateTimeOffset(1),
                SubjectId = rows.IsDBNull(2) ? (Guid?)null : rows.GetGuid(2), SessionId = rows.IsDBNull(3) ? (Guid?)null : rows.GetGuid(3),
                OperationId = rows.IsDBNull(4) ? (Guid?)null : rows.GetGuid(4), Action = rows.GetString(5), Outcome = rows.GetString(6) });
            return items;
        }, token);

    public Task<object> ReadAdministrationAsync(string secret, CancellationToken token) =>
        TransactionAsync("Shared", ctx =>
        {
            Require(ctx.State, secret, SecurityActions.ManageUsers, null);
            // No hashes, reset tokens, opaque session credentials, or email claims leave the authority.
            return Task.FromResult<object>(new { Revision = ctx.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), Users = ctx.State.Accounts.Select(a => new { a.Id, a.UserName, a.Disabled, a.MustChangePassword, a.Roles }).ToArray(),
                Roles = ctx.State.Roles.Select(r => new { r.Id, r.Name }).ToArray() });
        }, token, save: false);

    private AuthenticatedSession? Resolve(SecurityState state, string secret)
    {
        if (secret.Length != 64 || !secret.All(char.IsAsciiHexDigit)) return null;
        var hash = SecurityRules.TokenHash(secret);
        var session = state.Sessions.SingleOrDefault(s => s.TokenHash == hash);
        var account = state.Accounts.SingleOrDefault(a => a.Id == session?.SubjectId);
        return account is not null && session is not null && SecurityRules.SessionIsCurrent(account, session, clock.GetUtcNow())
            ? new(account.Id, session.Id, account.MustChangePassword) : null;
    }
    private AuthenticatedSession Require(SecurityState state, string secret, string action, Guid? machine)
    {
        var identity = Resolve(state, secret) ?? throw new UnauthorizedAccessException();
        if (identity.MustChangePassword || !SecurityRules.HasGrant(state, state.Accounts.Single(a => a.Id == identity.SubjectId), action, machine, at: clock.GetUtcNow()))
            throw new AuthorizationDeniedException(identity.SubjectId, identity.SessionId, action, machine);
        return identity;
    }
    private AuthenticatedSession RequireCommand(SecurityState state, string secret, SecurityCommand command, string permission) =>
        command.Action == "password.change" ? Resolve(state, secret) ?? throw new UnauthorizedAccessException() : Require(state, secret, permission, null);

    private static string Permission(string action) => action switch
    {
        "users.create" or "users.disable" or "sessions.revoke" => SecurityActions.ManageUsers,
        "roles.save" => SecurityActions.ManageRoles,
        "membership.set" => SecurityActions.GrantAccess,
        "password.change" => "password.change",
        _ => throw new ArgumentException("Unsupported action.", nameof(action)),
    };
    private SecurityResult Apply(SecurityState state, AuthenticatedSession identity, SecurityCommand command, long next, Guid allocatedId)
    {
        var actor = state.Accounts.Single(a => a.Id == identity.SubjectId);
        var target = state.Accounts.SingleOrDefault(a => a.Id == command.Target);
        switch (command.Action)
        {
            case "password.change":
                SecurityRules.ValidatePassword(command.Password ?? "");
                actor.PasswordHash = _hasher.HashPassword(actor, command.Password!); actor.MustChangePassword = false; actor.TemporaryCredentialExpiresAt = null;
                actor.SecurityStamp = Guid.NewGuid().ToString("N"); Revoke(state, actor.Id);
                return new("ChangedAndSessionsRevoked", actor.Id, next);
            case "users.create":
                var name = command.Name ?? ""; var normalized = SecurityRules.NormalizeName(name);
                SecurityRules.ValidatePassword(command.Password ?? "");
                if (state.Accounts.Any(a => a.NormalizedUserName == normalized)) return new("UsernameUnavailable");
                var user = NewAccount(name, normalized, command.Password!); user.Id = allocatedId; state.Accounts.Add(user);
                return new("Created", user.Id, next);
            case "users.disable":
                if (target is null || command.Disabled is null) return new("InvalidTarget");
                if (target.Id == actor.Id) return new("SelfAdministrationDenied");
                target.Disabled = command.Disabled.Value; Revoke(state, target.Id);
                return new("Updated", target.Id, next);
            case "sessions.revoke":
                if (target is null) return new("InvalidTarget"); Revoke(state, target.Id); return new("Revoked", target.Id, next);
            case "roles.save":
                if (command.Target is null || command.Target == Guid.Empty || string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 128 || command.Grants is null) return new("InvalidRequest");
                if (actor.Roles.Contains(command.Target.Value)) return new("SelfAdministrationDenied");
                if (command.Grants.Any(g => !ValidGrant(g) || !SecurityRules.CanDelegate(state, actor, g, clock.GetUtcNow())) ||
                    state.Roles.Where(r => r.Id == command.Target).SelectMany(r => r.Grants).Any(g => !SecurityRules.CanDelegate(state, actor, g, clock.GetUtcNow()))) return new("DelegationDenied");
                var role = new SecurityRole(command.Target.Value, command.Name, command.Grants.ToList());
                var index = state.Roles.FindIndex(r => r.Id == role.Id);
                if (index < 0) state.Roles.Add(role); else state.Roles[index] = role;
                return new("Updated", role.Id, next);
            case "membership.set":
                if (target is null || command.Roles is null || command.Roles.Any(id => !state.Roles.Any(r => r.Id == id))) return new("InvalidTarget");
                if (target.Id == actor.Id) return new("SelfAdministrationDenied");
                if (state.Roles.Where(r => command.Roles.Contains(r.Id) || target.Roles.Contains(r.Id)).SelectMany(r => r.Grants)
                    .Any(g => !SecurityRules.CanDelegate(state, actor, g, clock.GetUtcNow()))) return new("DelegationDenied");
                target.Roles = command.Roles.Distinct().ToList(); Revoke(state, target.Id); return new("Updated", target.Id, next);
            default: return new("InvalidRequest");
        }
    }
    private static bool ValidGrant(AccessGrant grant) => SecurityActions.Catalogue.Contains(grant.Action) &&
        (grant.Action == SecurityActions.ViewReports ? grant.MachineId is { } id && id != Guid.Empty : grant.MachineId is null) &&
        (grant.EffectiveFromUtc is null || grant.EffectiveFromUtc.Value.Offset == TimeSpan.Zero) &&
        (grant.ExpiresAtUtc is null || grant.ExpiresAtUtc.Value.Offset == TimeSpan.Zero) &&
        (grant.EffectiveFromUtc is null || grant.ExpiresAtUtc is null || grant.EffectiveFromUtc < grant.ExpiresAtUtc);
    private LocalAccount NewAccount(string name, string normalized, string password)
    {
        var user = new LocalAccount { Id = Guid.NewGuid(), UserName = name, NormalizedUserName = normalized,
            SecurityStamp = Guid.NewGuid().ToString("N"), MustChangePassword = true, TemporaryCredentialExpiresAt = clock.GetUtcNow() + SecurityRules.ResetLifetime, LockoutEnabled = true };
        user.PasswordHash = _hasher.HashPassword(user, password); return user;
    }
    private static void Revoke(SecurityState state, Guid subject)
    {
        for (var i = 0; i < state.Sessions.Count; i++)
            if (state.Sessions[i].SubjectId == subject) state.Sessions[i] = state.Sessions[i] with { Revoked = true };
    }

    private async Task<T> TransactionAsync<T>(string mode, Func<Context, Task<T>> work, CancellationToken token, bool save = true)
    {
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockAsync(connection, transaction, Gate, mode, token);
        // UPDLOCK for state mutations avoids shared->exclusive row lock conversion races.
        await using var read = new SqlCommand("SELECT Version, Revision, Payload FROM dbo.FactoryConnectSecurityState" + (save ? " WITH (UPDLOCK,HOLDLOCK)" : "") + " WHERE Id=1;", connection, transaction);
        SecurityState state; long revision; string originalPayload;
        await using (var rows = await read.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token) || rows.GetInt32(0) != 1) throw new InvalidDataException("Security authority unavailable.");
            revision = rows.GetInt64(1); originalPayload = rows.GetString(2); state = JsonSerializer.Deserialize<SecurityState>(originalPayload) ?? throw new InvalidDataException("Security authority invalid.");
        }
        var context = new Context(connection, transaction, state, revision, clock);
        T result;
        try { result = await work(context); }
        catch (UnauthorizedAccessException error)
        {
            // Never commit consumer writes on denial. Audit uses a separate short transaction.
            await transaction.RollbackAsync(CancellationToken.None);
            await using var auditConnection = new SqlConnection(connectionString);
            await auditConnection.OpenAsync(token);
            await using var auditTransaction = (SqlTransaction)await auditConnection.BeginTransactionAsync(token);
            await new Context(auditConnection, auditTransaction, new(), revision, clock)
                .AuditAsync((error as AuthorizationDeniedException)?.SubjectId, (error as AuthorizationDeniedException)?.SessionId, null,
                    (error as AuthorizationDeniedException)?.Action ?? "authorization", "Denied", token, (error as AuthorizationDeniedException)?.MachineId);
            await auditTransaction.CommitAsync(token);
            throw;
        }
        if (save && JsonSerializer.Serialize(state) != originalPayload)
        {
            await using var update = new SqlCommand("UPDATE dbo.FactoryConnectSecurityState SET Revision=Revision+1,Payload=@Payload WHERE Id=1 AND Version=1 AND Revision=@Revision;", connection, transaction);
            update.Parameters.AddWithValue("@Payload", JsonSerializer.Serialize(state)); update.Parameters.AddWithValue("@Revision", revision);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Security revision conflict.");
        }
        try { await transaction.CommitAsync(token); }
        catch (Exception error) when (error is SqlException or OperationCanceledException or InvalidOperationException)
        { throw new SecurityCommitOutcomeUnknownException(error); }
        return result;
    }
    private static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, string resource, string mode, CancellationToken token)
    {
        await using var cmd = new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@Resource,@LockMode=@Mode,@LockOwner='Transaction',@LockTimeout=5000,@DbPrincipal='public'; IF @r<0 THROW 51000,'Security lock unavailable',1;", connection, transaction);
        cmd.Parameters.AddWithValue("@Resource", resource); cmd.Parameters.AddWithValue("@Mode", mode); await cmd.ExecuteNonQueryAsync(token);
    }
    private static async Task RegisterAsync(Context ctx, Guid subject, Guid operation, byte[] bytes, CancellationToken token)
    {
        await using var cmd = new SqlCommand("SELECT Request FROM dbo.FactoryConnectSecurityOperation WITH(UPDLOCK,HOLDLOCK) WHERE SubjectId=@Subject AND OperationId=@Operation;", ctx.Connection, ctx.Transaction);
        cmd.Parameters.AddWithValue("@Subject", subject); cmd.Parameters.AddWithValue("@Operation", operation);
        if (await cmd.ExecuteScalarAsync(token) is byte[] existing)
        {
            if (!existing.AsSpan().SequenceEqual(bytes)) throw new SecurityOperationConflictException();
            return;
        }
        cmd.CommandText = "INSERT dbo.FactoryConnectSecurityOperation(SubjectId,OperationId,Request,RegisteredAt) VALUES(@Subject,@Operation,@Request,@Now);";
        cmd.Parameters.AddWithValue("@Request", bytes); cmd.Parameters.AddWithValue("@Now", ctx.Clock.GetUtcNow()); await cmd.ExecuteNonQueryAsync(token);
    }
    private static async Task<Guid> AllocatedIdAsync(Context ctx, Guid subject, Guid operation, CancellationToken token)
    {
        await using var cmd = new SqlCommand("SELECT AllocatedId FROM dbo.FactoryConnectSecurityOperation WHERE SubjectId=@Subject AND OperationId=@Operation;", ctx.Connection, ctx.Transaction);
        cmd.Parameters.AddWithValue("@Subject", subject); cmd.Parameters.AddWithValue("@Operation", operation);
        return (Guid)(await cmd.ExecuteScalarAsync(token) ?? throw new InvalidDataException("Missing registered allocation."));
    }
    private static async Task<SecurityResult?> ResultAsync(Context ctx, Guid subject, Guid operation, byte[] bytes, CancellationToken token)
    {
        await RegisterAsync(ctx, subject, operation, bytes, token);
        await using var cmd = new SqlCommand("SELECT Result FROM dbo.FactoryConnectSecurityOperation WHERE SubjectId=@Subject AND OperationId=@Operation;", ctx.Connection, ctx.Transaction);
        cmd.Parameters.AddWithValue("@Subject", subject); cmd.Parameters.AddWithValue("@Operation", operation);
        return await cmd.ExecuteScalarAsync(token) is string json ? JsonSerializer.Deserialize<SecurityResult>(json) ?? throw new InvalidDataException("Invalid stored result.") : null;
    }
    private static async Task CompleteAsync(Context ctx, Guid subject, Guid? session, Guid operation, string action, SecurityResult result, CancellationToken token)
    {
        await using var cmd = new SqlCommand("UPDATE dbo.FactoryConnectSecurityOperation SET Result=@Result,CompletedAt=@Now WHERE SubjectId=@Subject AND OperationId=@Operation AND Result IS NULL;", ctx.Connection, ctx.Transaction);
        cmd.Parameters.AddWithValue("@Subject", subject); cmd.Parameters.AddWithValue("@Operation", operation);
        cmd.Parameters.AddWithValue("@Result", JsonSerializer.Serialize(result)); cmd.Parameters.AddWithValue("@Now", ctx.Clock.GetUtcNow());
        if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Operation completion conflict.");
        await ctx.AuditAsync(subject, session, operation, action, result.Outcome, token);
    }
    private sealed record Context(SqlConnection Connection, SqlTransaction Transaction, SecurityState State, long Revision, TimeProvider Clock)
    {
        internal async Task AuditAsync(Guid? subject, Guid? session, Guid? operation, string action, string outcome, CancellationToken token, Guid? machine = null)
        {
            await using var cmd = new SqlCommand("INSERT dbo.FactoryConnectSecurityAudit(OccurredAt,SubjectId,SessionId,OperationId,Action,Outcome,MachineId,AuthorizationRevision) VALUES(@Now,@Subject,@Session,@Operation,@Action,@Outcome,@Machine,@Revision);", Connection, Transaction);
            cmd.Parameters.AddWithValue("@Now", Clock.GetUtcNow()); cmd.Parameters.AddWithValue("@Subject", (object?)subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Session", (object?)session ?? DBNull.Value); cmd.Parameters.AddWithValue("@Operation", (object?)operation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Machine", (object?)machine ?? DBNull.Value); cmd.Parameters.AddWithValue("@Revision", Revision);
            cmd.Parameters.AddWithValue("@Action", action); cmd.Parameters.AddWithValue("@Outcome", outcome); await cmd.ExecuteNonQueryAsync(token);
        }
    }
}
