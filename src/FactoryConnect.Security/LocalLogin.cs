using System.Text.Json;
using Microsoft.AspNetCore.Identity;
namespace FactoryConnect.Security;

public sealed partial class SqlSecurityAuthority
{
    private sealed record CredentialSnapshot(Guid AttemptId, LocalAccount? Account, bool Eligible);
    public async Task<(string? Token, string Outcome)> LoginAsync(string username, string password, CancellationToken token)
    {
        if (string.IsNullOrEmpty(username) || username.Length > 128 || password is null || password.Length > 256) return (null, "InvalidCredentials");
        var normalized = username.ToUpperInvariant();
        var snapshot = await TransactionAsync("Exclusive", async ctx =>
        {
            var now = clock.GetUtcNow();
            ctx.State.LoginAttempts.RemoveAll(a => a.Completed || now >= a.ExpiresAt);
            var window = ctx.State.LoginWindows.GetValueOrDefault("login");
            if (window is null || now >= window.StartedAt.AddMinutes(1)) window = new(now, 0);
            if (window.Attempts >= 30 || ctx.State.LoginAttempts.Count >= 8)
            {
                await ctx.AuditAsync(null, null, null, "login", "TryLater", token);
                return (CredentialSnapshot?)null;
            }
            ctx.State.LoginWindows["login"] = window with { Attempts = window.Attempts + 1 };
            var account = ctx.State.Accounts.SingleOrDefault(a => a.NormalizedUserName == normalized);
            var attempt = new LoginAttempt(Guid.NewGuid(), account?.Id, now.AddMinutes(2), false);
            ctx.State.LoginAttempts.Add(attempt);
            // Owned snapshot: no SQL connection, transaction or mutable shared account crosses verification.
            return new CredentialSnapshot(attempt.Id, account is null ? null : JsonSerializer.Deserialize<LocalAccount>(JsonSerializer.Serialize(account)), CanLogin(account, now));
        }, token, administration: false, phase: "login.reserve");
        if (snapshot is null) return (null, "TryLater");

        token.ThrowIfCancellationRequested();
        if (TestHooks?.BeforeCredentialVerification is { } hook) await hook(token);
        var check = _hasher.VerifyHashedPassword(snapshot.Account ?? new LocalAccount(), snapshot.Eligible ? snapshot.Account!.PasswordHash! : _dummyHash, password);
        if (!snapshot.Eligible) check = PasswordVerificationResult.Failed;
        var replacement = check == PasswordVerificationResult.SuccessRehashNeeded ? _hasher.HashPassword(snapshot.Account!, password) : null;
        token.ThrowIfCancellationRequested();
        return await TransactionAsync("Exclusive", async ctx =>
        {
            var now = clock.GetUtcNow();
            var attemptIndex = ctx.State.LoginAttempts.FindIndex(a => a.Id == snapshot.AttemptId);
            var attempt = attemptIndex < 0 ? null : ctx.State.LoginAttempts[attemptIndex];
            var account = ctx.State.Accounts.SingleOrDefault(a => a.Id == snapshot.Account?.Id);
            if (attempt is null || attempt.Completed || now >= attempt.ExpiresAt || !snapshot.Eligible || !CanLogin(account, now) ||
                account!.AuthenticationGeneration != snapshot.Account!.AuthenticationGeneration || account.SecurityStamp != snapshot.Account.SecurityStamp || account.PasswordHash != snapshot.Account.PasswordHash)
            {
                if (attemptIndex >= 0) ctx.State.LoginAttempts[attemptIndex] = attempt! with { Completed = true };
                await ctx.AuditAsync(account?.Id, null, null, "login", "InvalidCredentials", token);
                return ((string?)null, "InvalidCredentials");
            }
            ctx.State.LoginAttempts[attemptIndex] = attempt with { Completed = true };
            if (check == PasswordVerificationResult.Failed)
            {
                account.AccessFailedCount++;
                if (account.AccessFailedCount >= 5)
                { account.LockoutEnd = now.AddMinutes(15); account.AccessFailedCount = 0; account.AuthenticationGeneration++; }
                await ctx.AuditAsync(account.Id, null, null, "login", "InvalidCredentials", token);
                return ((string?)null, "InvalidCredentials");
            }
            if (ctx.State.Sessions.Count(s => s.SubjectId == account.Id && SecurityRules.SessionIsCurrent(account, s, now)) >= 5)
            {
                await ctx.AuditAsync(account.Id, null, null, "login", "SessionLimit", token);
                return ((string?)null, "TryLater");
            }
            if (replacement is not null) { account.PasswordHash = replacement; account.AuthenticationGeneration++; }
            account.AccessFailedCount = 0; account.LockoutEnd = null;
            var secret = SecurityRules.NewToken();
            var session = new LocalSession(Guid.NewGuid(), account.Id, SecurityRules.TokenHash(secret), now, now, false);
            ctx.State.Sessions.Add(session);
            await ctx.AuditAsync(account.Id, session.Id, null, "login", "Succeeded", token);
            return ((string?)secret, "Succeeded");
        }, token, administration: false, phase: "login.issue");
    }
    private static bool CanLogin(LocalAccount? account, DateTimeOffset now) => account is not null && !account.Disabled &&
        (!account.MustChangePassword || account.TemporaryCredentialExpiresAt is { } expiry && now < expiry) &&
        (account.LockoutEnd is null || now >= account.LockoutEnd) && !string.IsNullOrEmpty(account.PasswordHash);
    // Characterization only: exact pre-hardening hash-under-exclusive-gate cost,
    // not a public alternate login path and never used for issuance.
    internal Task<bool> BenchmarkLegacyHashAsync(string secret, string password, CancellationToken token) =>
        TransactionAsync("Exclusive", async ctx =>
        {
            var identity = Require(ctx.State, secret, SecurityActions.ManageUsers, null);
            var account = ctx.State.Accounts.Single(a => a.Id == identity.SubjectId);
            if (TestHooks?.BeforeCredentialVerification is { } hook) await hook(token);
            return _hasher.VerifyHashedPassword(account, account.PasswordHash!, password) != PasswordVerificationResult.Failed;
        }, token, save: false, phase: "login.legacy-benchmark");

}
