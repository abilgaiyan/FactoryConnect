using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.AspNetCore.Identity;

namespace FactoryConnect.Security;

// Local issuer identity is immutable; names/emails never establish the principal.
public sealed class LocalAccount : IdentityUser<Guid>
{
    public bool Disabled { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? TemporaryCredentialExpiresAt { get; set; }
    public List<Guid> Roles { get; set; } = [];
}
public sealed record AccessGrant(string Action, Guid? MachineId, bool Delegable, DateTimeOffset? EffectiveFromUtc = null, DateTimeOffset? ExpiresAtUtc = null);
public sealed record SecurityRole(Guid Id, string Name, List<AccessGrant> Grants);
public sealed record LocalSession(Guid Id, Guid SubjectId, string TokenHash, DateTimeOffset CreatedAt,
    DateTimeOffset LastInteractiveAt, bool Revoked);
public sealed record PasswordReset(Guid SubjectId, string TokenHash, DateTimeOffset ExpiresAt, bool Used, Guid OperationId = default, string? ProtectedToken = null, string? Email = null);
public sealed class SecurityState
{
    public List<LocalAccount> Accounts { get; set; } = [];
    public List<SecurityRole> Roles { get; set; } = [];
    public List<LocalSession> Sessions { get; set; } = [];
    public List<PasswordReset> Resets { get; set; } = [];
    public Dictionary<string, LoginWindow> LoginWindows { get; set; } = new(StringComparer.Ordinal);
}
public sealed record LoginWindow(DateTimeOffset StartedAt, int Attempts);
public sealed record SecurityResult(string Outcome, Guid? Id = null, [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long? Revision = null, bool Historical = false);
public sealed record SecurityCommand(Guid OperationId, [property: JsonConverter(typeof(CanonicalSecurityRevisionConverter))] long ExpectedRevision, string Action, Guid? Target,
    Guid? MachineId, string? Name, string? Password, AccessGrant[]? Grants, Guid[]? Roles, bool? Disabled);
public sealed record AuthenticatedSession(Guid SubjectId, Guid SessionId, bool MustChangePassword);
public static class SecurityActions
{
    public const string ViewReports = "reports.view";
    public const string ManageUsers = "security.users.manage";
    public const string ManageRoles = "security.roles.manage";
    public const string GrantAccess = "security.access.grant";
    public const string ViewAudit = "security.audit.view";
    public static IReadOnlySet<string> Catalogue { get; } = new HashSet<string>(StringComparer.Ordinal)
        { ViewReports, ManageUsers, ManageRoles, GrantAccess, ViewAudit };
}
public static class SecurityRules
{
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(15);
    public static bool SessionIsCurrent(LocalAccount account, LocalSession session, DateTimeOffset now) =>
        !account.Disabled && (!account.MustChangePassword || account.TemporaryCredentialExpiresAt is { } expiry && now < expiry) && !session.Revoked && session.SubjectId == account.Id &&
        now >= session.CreatedAt && now >= session.LastInteractiveAt &&
        now < session.CreatedAt + AbsoluteLifetime && now < session.LastInteractiveAt + IdleLifetime;
    public static bool HasGrant(SecurityState state, LocalAccount account, string action, Guid? machine, bool delegation = false, DateTimeOffset? at = null) =>
        state.Roles.Where(role => account.Roles.Contains(role.Id)).SelectMany(role => role.Grants)
            .Any(grant => grant.Action == action && grant.MachineId == machine && (!delegation || grant.Delegable) &&
                (grant.EffectiveFromUtc is null || grant.EffectiveFromUtc <= (at ?? DateTimeOffset.UtcNow)) &&
                (grant.ExpiresAtUtc is null || (at ?? DateTimeOffset.UtcNow) < grant.ExpiresAtUtc));
    public static bool CanDelegate(SecurityState state, LocalAccount account, AccessGrant proposed, DateTimeOffset now) =>
        state.Roles.Where(role => account.Roles.Contains(role.Id)).SelectMany(role => role.Grants).Any(grant =>
            grant.Delegable && grant.Action == proposed.Action && grant.MachineId == proposed.MachineId &&
            (grant.EffectiveFromUtc is null || grant.EffectiveFromUtc <= now) && (grant.ExpiresAtUtc is null || now < grant.ExpiresAtUtc) &&
            (grant.EffectiveFromUtc is null || proposed.EffectiveFromUtc is { } start && start >= grant.EffectiveFromUtc) &&
            (grant.ExpiresAtUtc is null || proposed.ExpiresAtUtc is { } end && end <= grant.ExpiresAtUtc));
    public static void ValidatePassword(string password)
    {
        if (password.Length is < 14 or > 256 || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password must contain 14–256 characters.", nameof(password));
    }
    public static string NormalizeName(string name)
    {
        if (name.Length is < 1 or > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("Invalid username.", nameof(name));
        return name.ToUpperInvariant();
    }
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    // v1 bytes: exact UTF8 System.Text.Json record order; nulls included, array order significant,
    // no text normalization. Persist full bytes, not just a fingerprint. Password is replaced
    // by a server-keyed HMAC before registration (never store plaintext or an offline verifier).
    public static byte[] CanonicalRequest(SecurityCommand command, byte[] requestKey)
    {
        var protectedCommand = command with { Password = command.Password is null ? null :
            Convert.ToHexString(HMACSHA256.HashData(requestKey, Encoding.UTF8.GetBytes(command.Password))) };
        return Encoding.UTF8.GetBytes("factoryconnect-security-command:v1\n" + JsonSerializer.Serialize(protectedCommand));
    }
}

public sealed class SecurityOperationConflictException : InvalidOperationException
{
    public SecurityOperationConflictException() : base("Operation content conflict.") { }
}

public sealed class CanonicalSecurityRevisionConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Revision must be a canonical decimal string.");
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > 1 && value[0] == '0' || value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) throw new JsonException("Invalid revision.");
        return revision;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

internal sealed class AuthorizationDeniedException(Guid subjectId, Guid sessionId, string action, Guid? machineId) : UnauthorizedAccessException
{
    internal Guid SubjectId { get; } = subjectId;
    internal Guid SessionId { get; } = sessionId;
    internal string Action { get; } = action;
    internal Guid? MachineId { get; } = machineId;
}

public sealed class SecurityCommitOutcomeUnknownException : InvalidOperationException
{
    public SecurityCommitOutcomeUnknownException(Exception inner) : base("Security commit outcome requires same-operation reconciliation.", inner) { }
}
