using System.Text.Json;
using FactoryConnect.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SupervisorSecuritySqlTests
{
    private const string Password = "initial-synthetic-password";
    private const string Changed = "changed-synthetic-password";
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    [Fact]
    public async Task BootstrapChangePasswordRestartReplayAndChangedContent()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync();
        var authority = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System);
        await authority.InstallAsync(default);
        var operation = Guid.NewGuid(); var machine = Guid.NewGuid();
        var first = await authority.BootstrapAsync(operation, "admin", Password, [machine], "synthetic approval", default);
        Assert.Equal("Created", first.Outcome);
        var replay = await authority.BootstrapAsync(operation, "admin", Password, [machine], "synthetic approval", default);
        Assert.True(replay.Historical); Assert.Equal(first.Id, replay.Id);
        await Assert.ThrowsAsync<SecurityOperationConflictException>(() => authority.BootstrapAsync(operation, "other", Password, [machine], "synthetic approval", default));
        var secret = await LoginAndChange(authority, db.ConnectionString);
        var command = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "users.create", null, null, "operator", Password, null, null, null);
        var created = await authority.ExecuteAsync(secret, command, default);
        var restarted = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System);
        var same = await restarted.ExecuteAsync(secret, command, default);
        Assert.True(same.Historical); Assert.Equal(created.Id, same.Id); Assert.Equal(created.Revision, same.Revision);
        await Assert.ThrowsAsync<SecurityOperationConflictException>(() => restarted.ExecuteAsync(secret, command with { Name = "different" }, default));
        Assert.Equal(2, await Scalar<int>(db.ConnectionString, "SELECT COUNT(*) FROM OPENJSON((SELECT Payload FROM dbo.FactoryConnectSecurityState WHERE Id=1),'$.Accounts');"));
        var snapshot = JsonSerializer.Serialize(await authority.ReadAdministrationAsync(secret, default));
        Assert.DoesNotContain("PasswordHash", snapshot, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ExecuteAuthorizedAsync(secret, SecurityActions.ViewReports, Guid.NewGuid(), (_, _, _, _) => Task.FromResult(true), default));
        Assert.True(await authority.ExecuteAuthorizedAsync(secret, SecurityActions.ViewReports, machine, (_, _, _, _) => Task.FromResult(true), default));
    }
    [Fact]
    public async Task ConcurrentEditsRequireExpectedHeadAndNeverOverwrite()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        var revision = await Revision(db.ConnectionString);
        var one = new SecurityCommand(Guid.NewGuid(), revision, "users.create", null, null, "one", Password, null, null, null);
        var two = one with { OperationId = Guid.NewGuid(), Name = "two" };
        var results = await Task.WhenAll(authority.ExecuteAsync(secret, one, default), authority.ExecuteAsync(secret, two, default));
        Assert.Single(results, r => r.Outcome == "Created"); Assert.Single(results, r => r.Outcome == "StaleRevision");
    }
    [Fact]
    public async Task SelfEscalationAndRevokedResultRetrievalAreDenied()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        var state = await State(db.ConnectionString); var admin = Assert.Single(state.Accounts); var role = Assert.Single(state.Roles);
        var edit = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "roles.save", role.Id, null, "renamed", null, role.Grants.ToArray(), null, null);
        Assert.Equal("SelfAdministrationDenied", (await authority.ExecuteAsync(secret, edit, default)).Outcome);
        var membership = edit with { OperationId = Guid.NewGuid(), Action = "membership.set", Target = admin.Id, Roles = [role.Id] };
        Assert.Equal("SelfAdministrationDenied", (await authority.ExecuteAsync(secret, membership, default)).Outcome);
        var revoke = edit with { OperationId = Guid.NewGuid(), Action = "sessions.revoke", Target = admin.Id };
        Assert.Equal("Revoked", (await authority.ExecuteAsync(secret, revoke, default)).Outcome);
        Assert.Null(await authority.AuthenticateAsync(secret, default));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ExecuteAsync(secret, edit, default));
    }
    [Fact]
    public async Task ExecutionRollbackRetainsRegistrationAndStableAllocatedIdentity()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        var command = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "users.create", null, null, "operator", Password, null, null, null);
        await Execute(db.ConnectionString, "CREATE TRIGGER dbo.SecurityAuditFailure ON dbo.FactoryConnectSecurityAudit AFTER INSERT AS THROW 51001,'synthetic injected failure',1;");
        await Assert.ThrowsAsync<SqlException>(() => authority.ExecuteAsync(secret, command, default));
        Assert.Single((await State(db.ConnectionString)).Accounts);
        Assert.Equal(1, await Scalar<int>(db.ConnectionString, "SELECT COUNT(*) FROM dbo.FactoryConnectSecurityOperation WHERE Result IS NULL;"));
        var allocated = await Scalar<Guid>(db.ConnectionString, "SELECT AllocatedId FROM dbo.FactoryConnectSecurityOperation WHERE Result IS NULL;");
        await Execute(db.ConnectionString, "DROP TRIGGER dbo.SecurityAuditFailure;");
        Assert.Equal(allocated, (await authority.ExecuteAsync(secret, command, default)).Id);
    }
    [Fact]
    public async Task DeniedConsumerRollsBackItsWritesButRetainsDenialAudit()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        await Execute(db.ConnectionString, "CREATE TABLE dbo.SyntheticConsumer(Value int NOT NULL);");
        var denialsBefore = await Scalar<int>(db.ConnectionString, "SELECT COUNT(*) FROM dbo.FactoryConnectSecurityAudit WHERE Outcome='Denied';");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ExecuteAuthorizedAsync<bool>(secret, SecurityActions.ManageUsers, null, async (c,t,_,ct) =>
        {
            await using var command = new SqlCommand("INSERT dbo.SyntheticConsumer VALUES(1);", c, t); await command.ExecuteNonQueryAsync(ct);
            throw new UnauthorizedAccessException();
        }, default));
        Assert.Equal(0, await Scalar<int>(db.ConnectionString, "SELECT COUNT(*) FROM dbo.SyntheticConsumer;"));
        Assert.Equal(denialsBefore + 1, await Scalar<int>(db.ConnectionString, "SELECT COUNT(*) FROM dbo.FactoryConnectSecurityAudit WHERE Outcome='Denied';"));
    }
    [Fact]
    public async Task SharedOperationCompletesBeforeExclusiveRevocationAndLaterReadsDeny()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString); var user = Assert.Single((await State(db.ConnectionString)).Accounts);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = authority.ExecuteAuthorizedAsync(secret, SecurityActions.ManageUsers, null, async (_, _, _, ct) =>
        { inside.SetResult(); await release.Task.WaitAsync(ct); return true; }, default);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var command = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "sessions.revoke", user.Id, null, null, null, null, null, null);
        var revocation = authority.ExecuteAsync(secret, command, default);
        release.SetResult(); Assert.True(await operation);
        Assert.Equal("Revoked", (await revocation).Outcome);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ExecuteAuthorizedAsync(secret, SecurityActions.ManageUsers, null, (_,_,_,_) => Task.FromResult(true), default));
    }
    [Fact]
    public async Task ResetIsSingleUseExpiresAndRevokesExistingSessions()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var clock = new SecurityClock();
        var authority = await Setup(db.ConnectionString, clock); var secret = await LoginAndChange(authority, db.ConnectionString);
        // Synthetic fixture supplies independently verified address; production has no verification endpoint yet.
        var state = await State(db.ConnectionString); var account = Assert.Single(state.Accounts); account.Email = "synthetic@example.invalid"; account.EmailConfirmed = true;
        await SaveFixture(db.ConnectionString, state);
        var delivery = new CaptureDelivery(); var protector = new EphemeralDataProtectionProvider().CreateProtector("synthetic-reset");
        var issuance = Guid.NewGuid(); await authority.IssuePasswordResetAsync(secret, account.Id, issuance, protector, delivery, default);
        var original = delivery.Token; await authority.IssuePasswordResetAsync(secret, account.Id, issuance, protector, delivery, default); Assert.Equal(original, delivery.Token);
        var consumption = Guid.NewGuid(); var reset = await authority.ResetPasswordAsync(original!, consumption, "another-synthetic-password", default);
        Assert.Equal("ChangedAndSessionsRevoked", reset.Outcome); Assert.Null(await authority.AuthenticateAsync(secret, default));
        Assert.True((await authority.ResetPasswordAsync(original!, consumption, "another-synthetic-password", default)).Historical);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ResetPasswordAsync(original!, Guid.NewGuid(), "another-synthetic-password", default));
        clock.Now = clock.Now.AddMinutes(15);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ResetPasswordAsync(original!, consumption, "another-synthetic-password", default));
    }
    private static async Task<SqlSecurityAuthority> Setup(string connection, TimeProvider? clock = null)
    {
        var authority = new SqlSecurityAuthority(connection, Key, clock ?? TimeProvider.System); await authority.InstallAsync(default);
        await authority.BootstrapAsync(Guid.NewGuid(), "admin", Password, [], "synthetic approval", default); return authority;
    }
    private static async Task<string> LoginAndChange(SqlSecurityAuthority authority, string connection)
    {
        var login = await authority.LoginAsync("admin", Password, default); Assert.NotNull(login.Token);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ReadAdministrationAsync(login.Token, default));
        var command = new SecurityCommand(Guid.NewGuid(), await Revision(connection), "password.change", null, null, null, Changed, null, null, null);
        await authority.ExecuteAsync(login.Token, command, default);
        Assert.Null(await authority.AuthenticateAsync(login.Token, default));
        return (await authority.LoginAsync("admin", Changed, default)).Token!;
    }
    private static async Task<long> Revision(string connection) => await Scalar<long>(connection, "SELECT Revision FROM dbo.FactoryConnectSecurityState WHERE Id=1;");
    private static async Task<SecurityState> State(string connection) => JsonSerializer.Deserialize<SecurityState>(await Scalar<string>(connection, "SELECT Payload FROM dbo.FactoryConnectSecurityState WHERE Id=1;"))!;
    private static async Task SaveFixture(string connection, SecurityState state)
    {
        await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand("UPDATE dbo.FactoryConnectSecurityState SET Payload=@Payload WHERE Id=1;", c);
        cmd.Parameters.AddWithValue("@Payload", JsonSerializer.Serialize(state)); await cmd.ExecuteNonQueryAsync();
    }
    private static async Task Execute(string connection, string sql) { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql,c); await cmd.ExecuteNonQueryAsync(); }
    private static async Task<T> Scalar<T>(string connection, string sql) { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql,c); return (T)(await cmd.ExecuteScalarAsync())!; }
    private sealed class SecurityClock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026,10,31,0,0,0,TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CaptureDelivery : IPasswordResetDelivery { public bool IsConfigured => true; public string? Token { get; private set; } public Task SendAsync(string verifiedEmail, string token, CancellationToken cancellationToken) { Token=token; return Task.CompletedTask; } }
}
