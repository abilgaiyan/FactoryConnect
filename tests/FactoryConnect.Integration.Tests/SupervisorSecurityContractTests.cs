using System.Text;
using FactoryConnect.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorSecurityContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(0, 0, true)] [InlineData(29, 0, true)] [InlineData(30, 0, false)]
    [InlineData(479, 470, true)] [InlineData(480, 479, false)] [InlineData(-1, 0, false)]
    public void ExactAbsoluteAndInteractiveIdleBoundaries(int minutes, int activity, bool expected)
    {
        var account = new LocalAccount { Id = Guid.NewGuid() };
        var session = new LocalSession(Guid.NewGuid(), account.Id, "hash", Now, Now.AddMinutes(activity), false);
        Assert.Equal(expected, SecurityRules.SessionIsCurrent(account, session, Now.AddMinutes(minutes)));
    }
    [Fact]
    public void DisabledRevokedAndCrossSubjectSessionsAreNeverEligible()
    {
        var account = new LocalAccount { Id = Guid.NewGuid() };
        var session = new LocalSession(Guid.NewGuid(), account.Id, "hash", Now, Now, false);
        Assert.False(SecurityRules.SessionIsCurrent(account, session with { Revoked = true }, Now));
        Assert.False(SecurityRules.SessionIsCurrent(account, session with { SubjectId = Guid.NewGuid() }, Now));
        account.Disabled = true; Assert.False(SecurityRules.SessionIsCurrent(account, session, Now));
    }
    [Fact]
    public void ScopeAndDelegationAreExactAndRoleNamesGiveNoAuthority()
    {
        var user = new LocalAccount { Id = Guid.NewGuid() }; var machine = Guid.NewGuid(); var role = Guid.NewGuid(); user.Roles.Add(role);
        var state = new SecurityState { Accounts = [user], Roles = [new(role, "Administrator", [new(SecurityActions.ViewReports, machine, false)])] };
        Assert.True(SecurityRules.HasGrant(state, user, SecurityActions.ViewReports, machine));
        Assert.False(SecurityRules.HasGrant(state, user, SecurityActions.ViewReports, Guid.NewGuid()));
        Assert.False(SecurityRules.HasGrant(state, user, SecurityActions.ViewReports, null));
        Assert.False(SecurityRules.HasGrant(state, user, SecurityActions.ViewReports, machine, true));
        Assert.False(SecurityRules.HasGrant(state, user, SecurityActions.ManageUsers, null));
    }
    [Fact]
    public void CanonicalBytesIncludeVersionNullsRevisionAndArrayOrderButNeverPassword()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var command = new SecurityCommand(Guid.Parse("11111111-1111-1111-1111-111111111111"), 7, "users.create", null, null, "User", "not-a-real-password", null, null, null);
        var bytes = SecurityRules.CanonicalRequest(command, key); var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal("EBEC447F461F62B11334C9487D4CC8527D50F399E84CC50F88134ED7FF3E6C67", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.StartsWith("factoryconnect-security-command:v1\n", text, StringComparison.Ordinal);
        Assert.Contains("\"ExpectedRevision\":\"7\"", text, StringComparison.Ordinal); Assert.Contains("\"Target\":null", text, StringComparison.Ordinal);
        Assert.DoesNotContain(command.Password!, text, StringComparison.Ordinal);
        Assert.Equal(bytes, SecurityRules.CanonicalRequest(command, key));
        Assert.NotEqual(bytes, SecurityRules.CanonicalRequest(command with { Name = "user" }, key));
        Assert.NotEqual(bytes, SecurityRules.CanonicalRequest(command with { Password = "changed-password" }, key));
        Assert.NotEqual(bytes, SecurityRules.CanonicalRequest(command with { ExpectedRevision = 8 }, key));
        Assert.NotEqual(bytes, SecurityRules.CanonicalRequest(command, new byte[32]));
    }
    [Fact]
    public void RevisionEncodingPreservesLargeValuesAndRejectsNumericOrNoncanonicalInputs()
    {
        var command = new SecurityCommand(Guid.NewGuid(), 9007199254740993L, "users.create", null, null, null, null, null, null, null);
        var json = System.Text.Json.JsonSerializer.Serialize(command);
        Assert.Contains("\"ExpectedRevision\":\"9007199254740993\"", json, StringComparison.Ordinal);
        Assert.Equal(command.ExpectedRevision, System.Text.Json.JsonSerializer.Deserialize<SecurityCommand>(json)!.ExpectedRevision);
        foreach (var value in new[] { "7", "\"07\"", "\"+7\"", "\" 7\"", "\"9223372036854775808\"" })
            Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<SecurityCommand>(json.Replace("\"9007199254740993\"", value, StringComparison.Ordinal)));
    }
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(7L)]
    [InlineData(9007199254740993L)]
    [InlineData(long.MaxValue)]
    public void RecordedResultsRoundTripThroughTheDurableJsonCodec(long? revision)
    {
        var original = new SecurityResult("Created", Guid.NewGuid(), revision);
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SecurityResult>(json);
        Assert.Equal(original, restored);
        var expected = revision is null ? "null" : "\"" + revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
        Assert.Contains("\"Revision\":" + expected, json, StringComparison.Ordinal);
        Assert.Equal(original with { Historical = true }, restored! with { Historical = true });
    }
    [Theory]
    [InlineData("7")]
    [InlineData("\"07\"")]
    [InlineData("\"+7\"")]
    [InlineData("\" 7\"")]
    [InlineData("\"9223372036854775808\"")]
    public void RecordedResultsRejectNoncanonicalRevisions(string value)
    {
        var json = "{\"Outcome\":\"Created\",\"Revision\":" + value + "}";
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<SecurityResult>(json));
    }
    [Fact]
    public void DelegationCannotOutliveItsAuthorityOrExpandScope()
    {
        var user = new LocalAccount { Id = Guid.NewGuid() }; var role = Guid.NewGuid(); var machine = Guid.NewGuid(); user.Roles.Add(role);
        var grant = new AccessGrant(SecurityActions.ViewReports, machine, true, Now, Now.AddHours(1));
        var state = new SecurityState { Accounts = [user], Roles = [new(role, "delegator", [grant])] };
        Assert.True(SecurityRules.CanDelegate(state, user, grant with { Delegable = false }, Now));
        Assert.False(SecurityRules.CanDelegate(state, user, grant with { ExpiresAtUtc = null }, Now));
        Assert.False(SecurityRules.CanDelegate(state, user, grant with { MachineId = Guid.NewGuid() }, Now));
        Assert.False(SecurityRules.HasGrant(state, user, grant.Action, machine, at: Now.AddHours(1)));
    }
    [Fact]
    public void FrameworkHasherUsesSaltAndVerifiesWithoutCustomPasswordCrypto()
    {
        var hasher = new PasswordHasher<LocalAccount>(); var user = new LocalAccount();
        var first = hasher.HashPassword(user, "long-synthetic-password"); var second = hasher.HashPassword(user, "long-synthetic-password");
        Assert.NotEqual(first, second);
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, first, "long-synthetic-password"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, first, "wrong"));
    }
    [Theory]
    [InlineData("")] [InlineData("short")] [InlineData("              ")]
    public void WeakPasswordsAreRejected(string value) => Assert.Throws<ArgumentException>(() => SecurityRules.ValidatePassword(value));
    [Fact]
    public void MissingOrNonHttpsConfigurationFailsClosed()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.ThrowsAny<Exception>(() => SecurityWebOptions.Read(empty));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SupervisorSecurity:CanonicalOrigin"] = "http://localhost" }).Build();
        Assert.Throws<InvalidOperationException>(() => SecurityWebOptions.Read(config));
    }
    [Fact]
    public void TokensAreRandomAndStoredAsDigest()
    {
        var first = SecurityRules.NewToken(); var second = SecurityRules.NewToken();
        Assert.Equal(64, first.Length); Assert.NotEqual(first, second); Assert.NotEqual(first, SecurityRules.TokenHash(first));
        Assert.Equal(SecurityRules.TokenHash(first), SecurityRules.TokenHash(first));
        Assert.Equal(TimeSpan.FromMinutes(15), SecurityRules.ResetLifetime);
    }
}
