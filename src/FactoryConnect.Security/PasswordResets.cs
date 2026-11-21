using Microsoft.AspNetCore.DataProtection;

namespace FactoryConnect.Security;

// SMTP adapter must be explicitly approved/configured. No fallback to logs or HTTP response.
public interface IPasswordResetDelivery
{
    bool IsConfigured { get; }
    Task SendAsync(string verifiedEmail, string token, CancellationToken cancellationToken);
}
public sealed partial class SqlSecurityAuthority
{
    public async Task<SecurityResult> IssuePasswordResetAsync(string secret, Guid target, Guid operation,
        IDataProtector protector, IPasswordResetDelivery delivery, CancellationToken token)
    {
        if (!delivery.IsConfigured || operation == Guid.Empty) throw new InvalidOperationException("Approved reset delivery unavailable.");
        var request = new SecurityCommand(operation, 0, "password.reset.issue", target, null, null, null, null, null, null);
        var bytes = SecurityRules.CanonicalRequest(request, _requestKey);
        await TransactionAsync("Exclusive", async ctx =>
        {
            var identity = Require(ctx.State, secret, SecurityActions.ManageUsers, null);
            await RegisterAsync(ctx, identity.SubjectId, operation, bytes, token);
            return true;
        }, token, save: false);
        var pending = await TransactionAsync("Exclusive", async ctx =>
        {
            var identity = Require(ctx.State, secret, SecurityActions.ManageUsers, null);
            var old = await ResultAsync(ctx, identity.SubjectId, operation, bytes, token);
            var account = ctx.State.Accounts.SingleOrDefault(a => a.Id == target);
            if (account is null || account.Disabled || !account.EmailConfirmed || string.IsNullOrWhiteSpace(account.Email)) throw new InvalidOperationException("Verified reset destination unavailable.");
            var reset = ctx.State.Resets.SingleOrDefault(r => r.OperationId == operation && r.SubjectId == target);
            if (old is not null && reset is null) throw new InvalidDataException("Reset issuance result lacks its retained delivery record.");
            if (reset is null)
            {
                var clear = SecurityRules.NewToken();
                reset = new(target, SecurityRules.TokenHash(clear), clock.GetUtcNow() + SecurityRules.ResetLifetime, false,
                    operation, protector.Protect(clear), account.Email);
                ctx.State.Resets.Add(reset);
                await CompleteAsync(ctx, identity.SubjectId, identity.SessionId, operation, request.Action, new("Registered", target), token);
            }
            if (reset.Invalidated || reset.Used || clock.GetUtcNow() >= reset.ExpiresAt) throw new InvalidOperationException("Reset no longer eligible for delivery.");
            return reset;
        }, token);
        // Durable outbox first; retries resend the SAME link, never create a replacement token.
        // External delivery cannot be atomic with SQL or retract bytes after revocation.
        await delivery.SendAsync(pending.Email!, protector.Unprotect(pending.ProtectedToken!), token);
        return new("DeliveryAttempted", target);
    }

    public Task<SecurityResult> ResetPasswordAsync(string resetToken, Guid operation, string password, CancellationToken token)
    {
        SecurityRules.ValidatePassword(password);
        if (operation == Guid.Empty || resetToken.Length != 64 || !resetToken.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid reset envelope.");
        var hash = SecurityRules.TokenHash(resetToken);
        var request = new SecurityCommand(operation, 0, "password.reset.consume", null, null, hash, password, null, null, null);
        var bytes = SecurityRules.CanonicalRequest(request, _requestKey);
        return TransactionAsync("Exclusive", async ctx =>
        {
            var index = ctx.State.Resets.FindIndex(r => r.TokenHash == hash);
            if (index < 0) throw new UnauthorizedAccessException();
            var reset = ctx.State.Resets[index];
            var account = ctx.State.Accounts.SingleOrDefault(a => a.Id == reset.SubjectId);
            if (account is null || account.Disabled || reset.Invalidated || clock.GetUtcNow() >= reset.ExpiresAt) throw new UnauthorizedAccessException();
            var old = await ResultAsync(ctx, account.Id, operation, bytes, token);
            if (old is not null) return old with { Historical = true };
            if (reset.Used) throw new UnauthorizedAccessException();
            account.PasswordHash = _hasher.HashPassword(account, password); account.SecurityStamp = Guid.NewGuid().ToString("N");
            account.MustChangePassword = false; account.TemporaryCredentialExpiresAt = null; account.AccessFailedCount = 0; account.LockoutEnd = null;
            CredentialChanged(ctx.State, account, reset.OperationId);
            for (var i = 0; i < ctx.State.Resets.Count; i++)
                if (ctx.State.Resets[i].SubjectId == account.Id) ctx.State.Resets[i] = ctx.State.Resets[i] with { Used = true };
            var result = new SecurityResult("ChangedAndSessionsRevoked", account.Id, ctx.Revision + 1);
            await CompleteAsync(ctx, account.Id, null, operation, request.Action, result, token);
            return result;
        }, token);
    }
}
