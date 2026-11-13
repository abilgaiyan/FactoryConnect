using System.Text.Json;
using FactoryConnect.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed partial class SupervisorSecuritySqlTests
{
    [Fact]
    public async Task HashUpgradeRejectsOldSnapshotButPreservesExistingSessions()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync();
        var authority = await Setup(db.ConnectionString);
        var existing = await LoginAndChange(authority, db.ConnectionString);
        var state = await State(db.ConnectionString);
        var account = Assert.Single(state.Accounts);
        var legacy = new PasswordHasher<LocalAccount>(Options.Create(new PasswordHasherOptions { IterationCount = 10000 }));
        account.PasswordHash = legacy.HashPassword(account, Changed);
        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded,
            new PasswordHasher<LocalAccount>().VerifyHashedPassword(account, account.PasswordHash, Changed));
        await SaveFixture(db.ConnectionString, state);
        var oldHash = account.PasswordHash; var generation = account.AuthenticationGeneration;
        var stamp = account.SecurityStamp; var revision = await Revision(db.ConnectionString);
        var reached = Signal(); var release = Signal();
        var racing = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { BeforeCredentialVerification = async ct => { reached.SetResult(); await release.Task.WaitAsync(ct); } } };
        var stale = racing.LoginAsync("admin", Changed, default);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull((await authority.LoginAsync("admin", Changed, default)).Token);
            var upgraded = Assert.Single((await State(db.ConnectionString)).Accounts);
            Assert.NotEqual(oldHash, upgraded.PasswordHash);
            Assert.Equal(generation + 1, upgraded.AuthenticationGeneration);
            Assert.Equal(stamp, upgraded.SecurityStamp);
            Assert.NotNull(await authority.AuthenticateAsync(existing, default));
        }
        finally { release.TrySetResult(); }
        Assert.Null((await stale.WaitAsync(TimeSpan.FromSeconds(10))).Token);
        var final = await State(db.ConnectionString); var current = Assert.Single(final.Accounts);
        Assert.Equal(2, final.Sessions.Count(s => SecurityRules.SessionIsCurrent(current, s, DateTimeOffset.UtcNow)));
        Assert.Equal(generation + 1, current.AuthenticationGeneration);
        Assert.Equal(revision, await Revision(db.ConnectionString));
        Assert.NotNull(await authority.AuthenticateAsync(existing, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetCommitFailureReconcilesUnderTargetSubject(bool acknowledgementLost)
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync();
        var authority = await Setup(db.ConnectionString);
        var existing = await LoginAndChange(authority, db.ConnectionString);
        var state = await State(db.ConnectionString); var account = Assert.Single(state.Accounts);
        account.Email = "synthetic@example.invalid"; account.EmailConfirmed = true;
        await SaveFixture(db.ConnectionString, state);
        var delivery = new CaptureDelivery();
        await authority.IssuePasswordResetAsync(existing, account.Id, Guid.NewGuid(),
            new EphemeralDataProtectionProvider().CreateProtector("reset-acceptance"), delivery, default);
        var before = await State(db.ConnectionString);
        var operation = Guid.NewGuid(); const string password = "reset-acceptance-password";
        using var cancellation = new CancellationTokenSource();
        var failing = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new()
            {
                BeforeCommit = (_,_,_,_) => { if (!acknowledgementLost) cancellation.Cancel(); return Task.CompletedTask; },
                AfterCommit = (_,_) => acknowledgementLost ? throw new IOException("synthetic reset acknowledgement loss") : Task.CompletedTask
            } };
        if (acknowledgementLost)
            await Assert.ThrowsAsync<SecurityCommitOutcomeUnknownException>(() => failing.ResetPasswordAsync(delivery.Token!, operation, password, default));
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing.ResetPasswordAsync(delivery.Token!, operation, password, cancellation.Token));
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await State(db.ConnectionString)));
            Assert.NotNull(await authority.AuthenticateAsync(existing, default));
            Assert.Equal("NotFound", (await authority.ReadPasswordResetResultAsync(existing, operation, default)).Status);
            Assert.Equal(0, await Scalar<int>(db.ConnectionString,
                $"SELECT COUNT(*) FROM dbo.FactoryConnectSecurityAudit WHERE OperationId='{operation:D}' AND Action='password.reset.consume';"));
        }
        var result = await authority.ResetPasswordAsync(delivery.Token!, operation, password, default);
        Assert.Equal(acknowledgementLost, result.Historical);
        Assert.Equal(account.Id, result.Id);
        var committed = await State(db.ConnectionString);
        Assert.Equal(account.AuthenticationGeneration + 1, Assert.Single(committed.Accounts).AuthenticationGeneration);
        Assert.All(committed.Resets, r => Assert.True(r.Used));
        Assert.Null(await authority.AuthenticateAsync(existing, default));
        var replay = await authority.ResetPasswordAsync(delivery.Token!, operation, password, default);
        Assert.Equal(result with { Historical = true }, replay);
        Assert.Equal(JsonSerializer.Serialize(committed), JsonSerializer.Serialize(await State(db.ConnectionString)));
        var current = (await authority.LoginAsync("admin", password, default)).Token!;
        var historical = await authority.ReadPasswordResetResultAsync(current, operation, default);
        Assert.Equal("Completed", historical.Status);
        Assert.Equal(result with { Historical = true }, historical.Result);
        Assert.Equal(1, await Scalar<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.FactoryConnectSecurityOperation WHERE SubjectId='{account.Id:D}' AND OperationId='{operation:D}' AND Result IS NOT NULL;"));
        Assert.Equal(1, await Scalar<int>(db.ConnectionString,
            $"SELECT COUNT(*) FROM dbo.FactoryConnectSecurityAudit WHERE SubjectId='{account.Id:D}' AND OperationId='{operation:D}' AND Action='password.reset.consume';"));
        state = await State(db.ConnectionString);
        Assert.Single(state.Roles).Grants.RemoveAll(g => g.Action == SecurityActions.RetrieveResetResult);
        await SaveFixture(db.ConnectionString, state);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ReadPasswordResetResultAsync(current, operation, default));
    }
}
