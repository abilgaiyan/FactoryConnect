using System.Diagnostics;
using System.Text.Json;
using FactoryConnect.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace FactoryConnect.Integration.Tests;

public sealed partial class SupervisorSecuritySqlTests
{
    private readonly ITestOutputHelper _output;
    public SupervisorSecuritySqlTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CredentialChangesAndRecoveryInvalidateAllResetTokensAndSessions(bool recovery)
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        var second = (await authority.LoginAsync("admin", Changed, default)).Token!;
        var state = await State(db.ConnectionString); var user = Assert.Single(state.Accounts);
        user.Email = "synthetic@example.invalid"; user.EmailConfirmed = true; await SaveFixture(db.ConnectionString, state);
        var protector = new EphemeralDataProtectionProvider().CreateProtector("hardening-reset");
        var first = new CaptureDelivery(); var other = new CaptureDelivery();
        await authority.IssuePasswordResetAsync(secret, user.Id, Guid.NewGuid(), protector, first, default);
        await authority.IssuePasswordResetAsync(secret, user.Id, Guid.NewGuid(), protector, other, default);
        if (recovery) await authority.RecoverAsync(Guid.NewGuid(), "admin", "recovered-synthetic-password", "synthetic approval", default);
        else await authority.ExecuteAsync(secret, new(Guid.NewGuid(), await Revision(db.ConnectionString), "password.change", null, null, null, "new-synthetic-password", null, null, null), default);
        Assert.Null(await authority.AuthenticateAsync(secret, default)); Assert.Null(await authority.AuthenticateAsync(second, default));
        foreach (var reset in new[] { first.Token, other.Token })
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ResetPasswordAsync(reset!, Guid.NewGuid(), "forbidden-reset-password", default));
        Assert.All((await State(db.ConnectionString)).Resets, r => Assert.True(r.Invalidated));
    }

    [Theory]
    [InlineData("password")] [InlineData("recovery")] [InlineData("disable")] [InlineData("lockout")]
    public async Task CredentialChangesDuringVerificationCannotIssueSession(string mutation)
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        var username = "admin"; var password = Changed; var subject = Assert.Single((await State(db.ConnectionString)).Accounts).Id;
        if (mutation == "disable")
        {
            var created = await authority.ExecuteAsync(secret, new(Guid.NewGuid(), await Revision(db.ConnectionString), "users.create", null, null, "operator", Password, null, null, null), default);
            subject = created.Id!.Value; username = "operator"; password = Password;
        }
        var reached = Signal(); var release = Signal();
        var racing = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { BeforeCredentialVerification = async ct => { reached.SetResult(); await release.Task.WaitAsync(ct); } } };
        var login = racing.LoginAsync(username, password, default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A real protected read finishes while verification is paused: no eligibility lock is held.
        Assert.True(await authority.ExecuteAuthorizedAsync(secret, SecurityActions.ManageUsers, null, (_,_,_,_) => Task.FromResult(true), default).WaitAsync(TimeSpan.FromSeconds(10)));
        switch (mutation)
        {
            case "password": await authority.ExecuteAsync(secret, new(Guid.NewGuid(), await Revision(db.ConnectionString), "password.change", null, null, null, "new-synthetic-password", null, null, null), default); break;
            case "recovery": await authority.RecoverAsync(Guid.NewGuid(), "admin", "recovered-synthetic-password", "synthetic approval", default); break;
            case "disable": await authority.ExecuteAsync(secret, new(Guid.NewGuid(), await Revision(db.ConnectionString), "users.disable", subject, null, null, null, null, null, true), default); break;
            case "lockout": for (var i = 0; i < 5; i++) Assert.Null((await authority.LoginAsync(username, "wrong-synthetic-password", default)).Token); break;
        }
        release.SetResult(); Assert.Null((await login).Token);
    }

    [Fact]
    public async Task LoginAndInteractiveActivityDoNotInvalidateAdministrationHead()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString); var revision = await Revision(db.ConnectionString);
        var other = await authority.LoginAsync("admin", Changed, default); Assert.NotNull(other.Token);
        await authority.SessionActionAsync(secret, "interactive", Guid.NewGuid(), default);
        Assert.Equal(revision, await Revision(db.ConnectionString));
        var command = new SecurityCommand(Guid.NewGuid(), revision, "users.create", null, null, "operator", Password, null, null, null);
        Assert.Equal("Created", (await authority.ExecuteAsync(secret, command, default)).Outcome);
        Assert.Equal("StaleRevision", (await authority.ExecuteAsync(secret, command with { OperationId = Guid.NewGuid(), Name = "another" }, default)).Outcome);
    }

    [Fact]
    public async Task LoginIsNonIdempotentButActiveSessionCreationIsBounded()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        _ = await LoginAndChange(authority, db.ConnectionString);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 4; i++) Assert.True(tokens.Add((await authority.LoginAsync("admin", Changed, default)).Token!));
        Assert.Null((await authority.LoginAsync("admin", Changed, default)).Token);
        var state = await State(db.ConnectionString); var account = Assert.Single(state.Accounts);
        Assert.Equal(5, state.Sessions.Count(s => SecurityRules.SessionIsCurrent(account, s, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task ExpiredResetRequiresCurrentActionGrantForHistoricalResult()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var clock = new SecurityClock();
        var authority = await Setup(db.ConnectionString, clock); var secret = await LoginAndChange(authority, db.ConnectionString);
        var state = await State(db.ConnectionString); var account = Assert.Single(state.Accounts); account.Email = "synthetic@example.invalid"; account.EmailConfirmed = true;
        await SaveFixture(db.ConnectionString, state);
        var delivery = new CaptureDelivery(); var protector = new EphemeralDataProtectionProvider().CreateProtector("hardening-reset");
        await authority.IssuePasswordResetAsync(secret, account.Id, Guid.NewGuid(), protector, delivery, default);
        var operation = Guid.NewGuid(); var original = await authority.ResetPasswordAsync(delivery.Token!, operation, "reset-synthetic-password", default);
        clock.Now = clock.Now.AddMinutes(15);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ResetPasswordAsync(delivery.Token!, operation, "reset-synthetic-password", default));
        var current = (await authority.LoginAsync("admin", "reset-synthetic-password", default)).Token!;
        var historical = await authority.ReadPasswordResetResultAsync(current, operation, default);
        Assert.Equal("Completed", historical.Status); Assert.Equal(original with { Historical = true }, historical.Result);
        Assert.DoesNotContain(delivery.Token!, JsonSerializer.Serialize(historical), StringComparison.Ordinal);
        // Fixture removes only the retrieval grant; session ownership alone is insufficient.
        state = await State(db.ConnectionString); var role = Assert.Single(state.Roles);
        role.Grants.RemoveAll(g => g.Action == SecurityActions.RetrieveResetResult); await SaveFixture(db.ConnectionString, state);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ReadPasswordResetResultAsync(current, operation, default));
    }

    [Fact]
    public async Task ResetResultLookupCannotReadAnotherSubjectOrAnotherAction()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority,db.ConnectionString);
        var command = new SecurityCommand(Guid.NewGuid(),await Revision(db.ConnectionString),"users.create",null,null,"operator",Password,null,null,null);
        await authority.ExecuteAsync(secret,command,default);
        Assert.Equal("NotFound",(await authority.ReadPasswordResetResultAsync(secret,command.OperationId,default)).Status);
        var otherOperation = Guid.NewGuid(); var other = Guid.NewGuid();
        var bytes = SecurityRules.CanonicalRequest(new(otherOperation,0,"password.reset.consume",null,null,null,null,null,null,null),Key);
        await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var insert = new SqlCommand("INSERT dbo.FactoryConnectSecurityOperation(SubjectId,OperationId,Request,Result,RegisteredAt,CompletedAt) VALUES(@Subject,@Operation,@Request,@Result,SYSUTCDATETIME(),SYSUTCDATETIME());",connection);
        insert.Parameters.AddWithValue("@Subject",other); insert.Parameters.AddWithValue("@Operation",otherOperation);
        insert.Parameters.AddWithValue("@Request",bytes); insert.Parameters.AddWithValue("@Result",JsonSerializer.Serialize(new SecurityResult("other-user-marker",other,1)));
        await insert.ExecuteNonQueryAsync();
        Assert.Equal("NotFound",(await authority.ReadPasswordResetResultAsync(secret,otherOperation,default)).Status);
    }

    [Fact]
    public async Task CancellationDuringVerificationIssuesNoSessionAndTicketExpires()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var clock = new SecurityClock();
        var authority = await Setup(db.ConnectionString,clock); _ = await LoginAndChange(authority,db.ConnectionString);
        var reached=Signal(); var release=Signal(); using var cancellation=new CancellationTokenSource();
        var racing = new SqlSecurityAuthority(db.ConnectionString,Key,clock)
        { TestHooks = new() { BeforeCredentialVerification = async ct => { reached.SetResult(); await release.Task.WaitAsync(ct); } } };
        var login=racing.LoginAsync("admin",Changed,cancellation.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        var state=await State(db.ConnectionString); var account=Assert.Single(state.Accounts);
        Assert.Single(state.Sessions,s=>SecurityRules.SessionIsCurrent(account,s,clock.Now));
        Assert.Single(state.LoginAttempts,a=>!a.Completed);
        clock.Now=clock.Now.AddMinutes(2);
        Assert.NotNull((await authority.LoginAsync("admin",Changed,default)).Token);
        Assert.DoesNotContain((await State(db.ConnectionString)).LoginAttempts,a=>!a.Completed);
    }

    [Fact]
    public async Task CancellationBeforeCommitRetainsRegistrationAndRollsBackExecution()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString); using var cancellation = new CancellationTokenSource();
        var failing = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { BeforeCommit = (_,_,phase,_) => { if (phase == "command") cancellation.Cancel(); return Task.CompletedTask; } } };
        var request = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "users.create", null, null, "operator", Password, null, null, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing.ExecuteAsync(secret, request, cancellation.Token));
        Assert.Single((await State(db.ConnectionString)).Accounts);
        Assert.Equal(1, await Scalar<int>(db.ConnectionString, "SELECT COUNT(*) FROM dbo.FactoryConnectSecurityOperation WHERE Result IS NULL;"));
        Assert.Equal("Created", (await authority.ExecuteAsync(secret, request, default)).Outcome);
    }

    [Fact]
    public async Task LostCommitAcknowledgementReturnsOriginalResultWithoutSecondMutation()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        var failing = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { AfterCommit = (phase,_) => phase == "command" ? throw new IOException("synthetic lost acknowledgement") : Task.CompletedTask } };
        var request = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "users.create", null, null, "operator", Password, null, null, null);
        await Assert.ThrowsAsync<SecurityCommitOutcomeUnknownException>(() => failing.ExecuteAsync(secret, request, default));
        var result = await authority.ExecuteAsync(secret, request, default); Assert.True(result.Historical); Assert.Equal("Created", result.Outcome);
        Assert.Equal(2, (await State(db.ConnectionString)).Accounts.Count);
        Assert.Equal(result.Id, (await authority.ExecuteAsync(secret, request, default)).Id);
    }

    [Fact]
    public async Task DenialAuditFailureCannotCommitConsumerWritesOrBecomeSuccess()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        await Execute(db.ConnectionString, "CREATE TABLE dbo.SyntheticDenied(Value int NOT NULL);");
        var failing = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { BeforeDenialAudit = _ => throw new IOException("synthetic audit failure") } };
        await Assert.ThrowsAsync<SecurityDenialAuditException>(() => failing.ExecuteAuthorizedAsync<bool>(secret, SecurityActions.ManageUsers, null, async (c,t,_,ct) =>
        {
            await using var cmd = new SqlCommand("INSERT dbo.SyntheticDenied VALUES(1);", c,t); await cmd.ExecuteNonQueryAsync(ct);
            throw new UnauthorizedAccessException();
        }, default));
        Assert.Equal(0, await Scalar<int>(db.ConnectionString, "SELECT COUNT(*) FROM dbo.SyntheticDenied;"));
    }

    [Fact]
    public async Task ApplicationLockWaitProvesOperationBeforeRevocationOrdering()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString); var subject = Assert.Single((await State(db.ConnectionString)).Accounts).Id;
        var inside = Signal(); var release = Signal(); var session = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = authority.ExecuteAuthorizedAsync(secret, SecurityActions.ManageUsers, null, async (_,_,_,ct) => { inside.SetResult(); await release.Task.WaitAsync(ct); return true; }, default);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var revoker = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { BeforeGate = (c,_,phase,_) => { if (phase == "registration") session.TrySetResult(c.ServerProcessId); return Task.CompletedTask; } } };
        var request = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "sessions.revoke", subject, null, null, null, null, null, null);
        var revocation = revoker.ExecuteAsync(secret, request, default);
        try { await WaitForLock(db.ConnectionString, await session.Task.WaitAsync(TimeSpan.FromSeconds(10)), application: true); }
        finally { release.TrySetResult(); }
        Assert.True(await operation); Assert.Equal("Revoked", (await revocation).Outcome);
        var called = false;
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => authority.ExecuteAuthorizedAsync(secret, SecurityActions.ManageUsers, null, (_,_,_,_) => { called=true; return Task.FromResult(true); }, default));
        Assert.False(called); // revocation-first ordering: no protected callback/source work.
    }

    [Fact]
    public async Task DeadlockVictimRollsBackCommandAndRetainsItsRequestForRetry()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        await Execute(db.ConnectionString, "CREATE TABLE dbo.SyntheticDeadlock(Id int PRIMARY KEY,V int NOT NULL); INSERT dbo.SyntheticDeadlock VALUES(1,0),(2,0);");
        var first = Signal(); var second = Signal(); var cycle = Signal(); var externalStarted = Signal();
        await using var external = new SqlConnection(db.ConnectionString); await external.OpenAsync();
        await using var externalTransaction = (SqlTransaction)await external.BeginTransactionAsync();
        await using (var cmd = new SqlCommand("UPDATE dbo.SyntheticDeadlock SET V=V+1 WHERE Id=2;",external,externalTransaction)) await cmd.ExecuteNonQueryAsync();
        second.SetResult();
        var victim = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { BeforeCommit = async (connection,transaction,phase,ct) =>
            {
                if (phase != "command") return;
                await using var a = new SqlCommand("SET DEADLOCK_PRIORITY LOW; UPDATE dbo.SyntheticDeadlock SET V=V+1 WHERE Id=1;",connection,transaction); await a.ExecuteNonQueryAsync(ct);
                first.SetResult(); await second.Task.WaitAsync(ct); await cycle.Task.WaitAsync(ct);
                await using var b = new SqlCommand("UPDATE dbo.SyntheticDeadlock SET V=V+1 WHERE Id=2;",connection,transaction); await b.ExecuteNonQueryAsync(ct);
            } } };
        var request = new SecurityCommand(Guid.NewGuid(), await Revision(db.ConnectionString), "users.create", null, null, "operator", Password, null, null, null);
        var pending = victim.ExecuteAsync(secret, request, default);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(15));
        async Task Compete()
        {
            await using var command = new SqlCommand("UPDATE dbo.SyntheticDeadlock SET V=V+1 WHERE Id=1;",external,externalTransaction);
            externalStarted.SetResult(); await command.ExecuteNonQueryAsync(); await externalTransaction.CommitAsync();
        }
        var competitor = Compete(); await externalStarted.Task;
        try { await WaitForLock(db.ConnectionString, external.ServerProcessId, application:false); }
        finally { cycle.TrySetResult(); }
        var error = await Assert.ThrowsAsync<SqlException>(() => pending); Assert.Equal(1205,error.Number);
        await competitor;
        Assert.Single((await State(db.ConnectionString)).Accounts);
        Assert.Equal(1,await Scalar<int>(db.ConnectionString,"SELECT COUNT(*) FROM dbo.FactoryConnectSecurityOperation WHERE Result IS NULL;"));
        Assert.Equal("Created",(await authority.ExecuteAsync(secret,request,default)).Outcome);
    }

    [Fact]
    public async Task ConcurrentUnknownLoginsRespectGlobalReservationLimit()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var reached = Signal(); var release = Signal(); var active = 0;
        var racing = new SqlSecurityAuthority(db.ConnectionString, Key, TimeProvider.System)
        { TestHooks = new() { BeforeCredentialVerification = async ct =>
            { if (Interlocked.Increment(ref active)==8) reached.TrySetResult(); await release.Task.WaitAsync(ct); } } };
        var attempts = Enumerable.Range(0,8).Select(i => racing.LoginAsync("unknown"+i,Password,default)).ToArray();
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var rejected = await racing.LoginAsync("unknown-overflow",Password,default);
            Assert.Equal("TryLater",rejected.Outcome); Assert.Null(rejected.Token);
            Assert.Equal(8,(await State(db.ConnectionString)).LoginAttempts.Count(a => !a.Completed));
        }
        finally { release.TrySetResult(); }
        Assert.All(await Task.WhenAll(attempts), result => Assert.Null(result.Token));
    }

    [Fact]
    [Trait("Evidence", "Performance")]
    public async Task RecordCoordinatedGateContentionForLegacyAndCandidateVerification()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        foreach (var legacy in new[] { true,false })
        {
            var verifying = Signal(); var release = Signal();
            var paused = new SqlSecurityAuthority(db.ConnectionString,Key,TimeProvider.System)
            { TestHooks = new() { BeforeCredentialVerification = async ct => { verifying.SetResult(); await release.Task.WaitAsync(ct); } } };
            Task verification = legacy ? paused.BenchmarkLegacyHashAsync(secret,Changed,default) : paused.LoginAsync("admin",Changed,default);
            await verifying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var spid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = new SqlSecurityAuthority(db.ConnectionString,Key,TimeProvider.System)
            { TestHooks = new() { BeforeGate = (c,_,_,_) => { spid.TrySetResult(c.ServerProcessId); return Task.CompletedTask; } } };
            var start = Stopwatch.GetTimestamp();
            var read = reader.ExecuteAuthorizedAsync(secret,SecurityActions.ManageUsers,null,(_,_,_,_) => Task.FromResult(true),default);
            try
            {
                if (legacy) await WaitForLock(db.ConnectionString,await spid.Task.WaitAsync(TimeSpan.FromSeconds(10)),application:true);
                else Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally { release.TrySetResult(); }
            Assert.True(await read); var readMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds; await verification;
            _output.WriteLine(JsonSerializer.Serialize(new { Legacy=legacy, CoordinatedProtectedReadMs=readMs,
                Note="Includes explicit coordination pause/DMV observation; proves lock ownership, not production latency or speedup." }));
        }
    }

    [Fact]
    [Trait("Evidence", "Performance")]
    public async Task RecordLegacyExclusiveHashAndCandidateLoginTimingsWithoutThresholdAssertions()
    {
        await using var db = await SqlStartupIsolatedDatabase.CreateAsync(); var authority = await Setup(db.ConnectionString);
        var secret = await LoginAndChange(authority, db.ConnectionString);
        Assert.True(await authority.BenchmarkLegacyHashAsync(secret, Changed, default));
        Assert.NotNull((await authority.LoginAsync("admin", Changed, default)).Token);
        for (var i=0; i<3; i++)
        {
            var start = Stopwatch.GetTimestamp(); Assert.True(await authority.BenchmarkLegacyHashAsync(secret, Changed, default));
            var legacy = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp(); Assert.NotNull((await authority.LoginAsync("admin", Changed, default)).Token);
            var candidate = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _output.WriteLine(JsonSerializer.Serialize(new { Measurement = i, LegacyExclusiveHashTransactionMs = legacy, CandidateCompleteLoginMs = candidate,
                Note = "Different scopes: characterization only, no performance assertion or production speedup." }));
        }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitForLock(string connection, int session, bool application)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var c = new SqlConnection(connection); await c.OpenAsync(timeout.Token);
        while (true)
        {
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id=@Session AND request_status='WAIT'" + (application ? " AND resource_type='APPLICATION'" : "") + ";",c);
            cmd.Parameters.AddWithValue("@Session",session);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(timeout.Token), System.Globalization.CultureInfo.InvariantCulture)>0) return;
            await Task.Yield(); // bounded DMV observation; no sleep-based ordering claim.
        }
    }
}
