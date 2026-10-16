using FactoryConnect.Abstractions;
using FactoryConnect.Core.AssumedBreakAuthority;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerAssumedBreakAuthorityIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
    private static AssumedBreakConfigurationSnapshot Snapshot() => new(new SiteId("GG"),
        new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f")), new DateOnly(2026, 10, 16),
        Start, Start.AddHours(8), "SHIFT-1", "schedule", "schedule-v1", "calendar-v1", "policy", "policy-v1",
        new DateOnly(2026, 10, 16), null, TimeSpan.FromMinutes(30), "demo instruction", ExclusionManifestState.CompleteEmpty, [], ["schedule-source", "calendar-source", "policy-source"]);
    private static AssumedBreakConfigurationAdmission Admission(AssumedBreakConfigurationSnapshot s) => new(s.Identity,
        s.SiteId, s.MachineId, s.ProductionDate, s.ProductionDate.AddDays(1), BreakAuthorityAdmissionMode.DemoProvisional,
        "demo-authority", "instruction-reference", ["admission-source"]);

    [Fact]
    public async Task RoundTripRestartHistoricalVersionsAndExactSelection()
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var s = Snapshot(); var a = Admission(s);
        Assert.Equal(BreakAuthorityPublicationOutcome.Appended, await store.PublishSnapshotAsync(s, s.Identity, CancellationToken.None));
        Assert.Equal(BreakAuthorityPublicationOutcome.Appended, await store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None));
        var changed = s with { ScheduleVersion = "schedule-v2", CalendarVersion = "calendar-v2", PolicyVersion = "policy-v2" };
        var changedAdmission = Admission(changed);
        await store.PublishSnapshotAsync(changed, changed.Identity, CancellationToken.None);
        await store.PublishAdmissionAsync(changedAdmission, changedAdmission.Identity, CancellationToken.None);
        var restarted = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        Assert.Equal(BreakAuthorityPublicationOutcome.Replayed, await restarted.PublishSnapshotAsync(s, s.Identity, CancellationToken.None));
        Assert.Equal(BreakAuthorityPublicationOutcome.Replayed, await restarted.PublishAdmissionAsync(a, a.Identity, CancellationToken.None));
        var old = await restarted.ReadSnapshotAsync(s.Identity, CancellationToken.None);
        Assert.Equal(BreakAuthorityReadOutcome.Found, old.Outcome);
        Assert.Equal(AssumedBreakAuthorityV1Codec.Encode(s), AssumedBreakAuthorityV1Codec.Encode(old.Value!));
        Assert.Equal(a.Identity, (await restarted.ReadAdmissionAsync(a.Identity, CancellationToken.None)).Value?.Identity);
        Assert.Equal(a.Identity, (await restarted.SelectAdmissionAsync(s.Identity, s.SiteId, s.MachineId, s.ProductionDate, CancellationToken.None)).Value?.Identity);
        Assert.Equal(changedAdmission.Identity, (await restarted.SelectAdmissionAsync(changed.Identity, s.SiteId, s.MachineId, s.ProductionDate, CancellationToken.None)).Value?.Identity);
        Assert.Equal(BreakAuthorityReadOutcome.Absent, (await restarted.ReadSnapshotAsync((s with { ScheduleVersion = "unpublished" }).Identity, CancellationToken.None)).Outcome);
        Assert.Equal(BreakAuthorityReadOutcome.IncomparableEvidence,
            (await restarted.SelectAdmissionAsync(s.Identity, s.SiteId, s.MachineId, s.ProductionDate.AddDays(1), CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndDuplicateBreakAuthorityCannotBeAdmitted(bool actualBreak)
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var s = actualBreak ? Snapshot() with { ExclusionState = ExclusionManifestState.CompleteIntervals,
            Exclusions = [new("break", Start.AddHours(1), Start.AddMinutes(90), AuthorityExclusionClassification.ActualBreak)] }
            : Snapshot() with { ExclusionState = ExclusionManifestState.Missing };
        var a = Admission(s);
        await store.PublishSnapshotAsync(s, s.Identity, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None).AsTask());
        Assert.Equal(BreakAuthorityReadOutcome.Absent, (await store.ReadAdmissionAsync(a.Identity, CancellationToken.None)).Outcome);
        Assert.Equal(BreakAuthorityReadOutcome.Found, (await store.ReadSnapshotAsync(s.Identity, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task AdmissionRequiresExactSnapshotAndRejectsAmbiguousCompetingAuthorities()
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var s = Snapshot(); var a = Admission(s);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None).AsTask());
        await store.PublishSnapshotAsync(s, s.Identity, CancellationToken.None);
        Assert.Equal(BreakAuthorityReadOutcome.Absent,
            (await store.SelectAdmissionAsync(s.Identity, s.SiteId, s.MachineId, s.ProductionDate, CancellationToken.None)).Outcome);
        await store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None);
        var competitor = a with { AdmittingAuthority = "competing-authority" };
        await store.PublishAdmissionAsync(competitor, competitor.Identity, CancellationToken.None);
        Assert.Equal(BreakAuthorityReadOutcome.Ambiguous,
            (await store.SelectAdmissionAsync(s.Identity, s.SiteId, s.MachineId, s.ProductionDate, CancellationToken.None)).Outcome);
        Assert.Equal(a.Identity, (await store.ReadAdmissionAsync(a.Identity, CancellationToken.None)).Value?.Identity);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentReplayAndExpectedIdentityConflictsNeverMutate(bool admissionRecord, bool conflict)
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var s = Snapshot(); var a = Admission(s);
        if (admissionRecord) await store.PublishSnapshotAsync(s, s.Identity, CancellationToken.None);
        async Task<string> Publish(bool second)
        {
            try
            {
                var publisher = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
                return (admissionRecord
                    ? await publisher.PublishAdmissionAsync(second && conflict ? a with { AdmittingAuthority = "changed" } : a, a.Identity, CancellationToken.None)
                    : await publisher.PublishSnapshotAsync(second && conflict ? s with { ScheduleVersion = "changed" } : s, s.Identity, CancellationToken.None)).ToString();
            }
            catch (InvalidDataException) { return "Conflict"; }
        }
        var outcomes = await Task.WhenAll(Publish(false), Publish(true));
        Assert.Single(outcomes, x => x == "Appended");
        Assert.Contains(conflict ? "Conflict" : "Replayed", outcomes);
        if (admissionRecord)
            Assert.Equal(a.Identity, (await store.ReadAdmissionAsync(a.Identity, CancellationToken.None)).Value?.Identity);
        else
            Assert.Equal(s.Identity, (await store.ReadSnapshotAsync(s.Identity, CancellationToken.None)).Value?.Identity);
    }

    [Theory]
    [InlineData(false, "PayloadBinary = 0x01", BreakAuthorityReadOutcome.Corrupt)]
    [InlineData(false, "ContentHash = 0x01", BreakAuthorityReadOutcome.Corrupt)]
    [InlineData(false, "IdentityBinary = 0x01", BreakAuthorityReadOutcome.Corrupt)]
    [InlineData(false, "PayloadCodecVersion = 2", BreakAuthorityReadOutcome.UnsupportedEncoding)]
    [InlineData(true, "PayloadBinary = 0x01", BreakAuthorityReadOutcome.Corrupt)]
    [InlineData(true, "IdentityCodecVersion = 2", BreakAuthorityReadOutcome.UnsupportedEncoding)]
    public async Task CorruptOrUnsupportedRecordsNeverFallbackOrReplay(bool admissionRecord, string mutation, BreakAuthorityReadOutcome expected)
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var s = Snapshot(); var a = Admission(s);
        await store.PublishSnapshotAsync(s, s.Identity, CancellationToken.None);
        await store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None);
        var table = admissionRecord ? "AssumedBreakConfigurationAdmission" : "AssumedBreakConfigurationSnapshot";
        await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"UPDATE dbo.{table} SET {mutation};";
        await command.ExecuteNonQueryAsync();
        var outcome = admissionRecord ? (await store.ReadAdmissionAsync(a.Identity, CancellationToken.None)).Outcome
            : (await store.ReadSnapshotAsync(s.Identity, CancellationToken.None)).Outcome;
        Assert.Equal(expected, outcome);
        if (!admissionRecord) Assert.Equal(expected, (await store.ReadAdmissionAsync(a.Identity, CancellationToken.None)).Outcome);
        if (expected == BreakAuthorityReadOutcome.UnsupportedEncoding)
            await Assert.ThrowsAsync<NotSupportedException>(() => store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None).AsTask());
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EffectiveScopeValidationAndUnionSurviveSqlRoundTrip()
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var s = Snapshot() with { ExclusionState = ExclusionManifestState.CompleteIntervals,
            Exclusions = [new("A", Start.AddHours(1), Start.AddMinutes(80), AuthorityExclusionClassification.OtherExclusion),
                          new("B", Start.AddMinutes(70), Start.AddMinutes(90), AuthorityExclusionClassification.OtherExclusion)] };
        await store.PublishSnapshotAsync(s, s.Identity, CancellationToken.None);
        var a = Admission(s);
        foreach (var invalid in new[] { a with { EffectiveFrom = s.ProductionDate.AddDays(1), EffectiveTo = null },
            a with { EffectiveFrom = s.ProductionDate.AddDays(-1), EffectiveTo = s.ProductionDate },
            a with { SiteId = new SiteId("other") } })
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAdmissionAsync(invalid, invalid.Identity, CancellationToken.None).AsTask());
        await store.PublishAdmissionAsync(a, a.Identity, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(30), (await store.ReadSnapshotAsync(s.Identity, CancellationToken.None)).Value!.ExcludedDuration());
    }

    [Fact]
    public async Task SelectionProtectsSnapshotWhileBlockedOnAdmissionCandidates()
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var snapshot = Snapshot(); var admission = Admission(snapshot);
        await store.PublishSnapshotAsync(snapshot, snapshot.Identity, CancellationToken.None);
        await store.PublishAdmissionAsync(admission, admission.Identity, CancellationToken.None);
        var applicationName = "AuthoritySelection_" + Guid.NewGuid().ToString("N");
        var selectedStore = new SqlServerAssumedBreakAuthorityStore(new SqlConnectionStringBuilder(db.ConnectionString)
            { ApplicationName = applicationName }.ConnectionString);
        await using var blocker = new SqlConnection(db.ConnectionString); await blocker.OpenAsync();
        await using var barrier = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var command = new SqlCommand(
            "SELECT IdentityHash FROM dbo.AssumedBreakConfigurationAdmission WITH (XLOCK, HOLDLOCK);", blocker, barrier))
            await command.ExecuteScalarAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var selection = selectedStore.SelectAdmissionAsync(snapshot.Identity, snapshot.SiteId, snapshot.MachineId,
            snapshot.ProductionDate, timeout.Token).AsTask();
        try
        {
            var waiting = false;
            for (var attempt = 0; attempt < 100 && !waiting; attempt++)
            {
                await using var command = new SqlCommand("""
                    SELECT COUNT(*) FROM sys.dm_exec_requests r
                    JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
                    WHERE s.program_name = @Application AND r.wait_type LIKE N'LCK%';
                    """, blocker, barrier);
                command.Parameters.Add("@Application", System.Data.SqlDbType.NVarChar, 128).Value = applicationName;
                waiting = (int)(await command.ExecuteScalarAsync(timeout.Token))! > 0;
                if (!waiting) await Task.Delay(50, timeout.Token);
            }
            Assert.True(waiting, "Selector must be observed waiting on the admission barrier.");
            Assert.False(selection.IsCompleted);
            await using var probe = new SqlConnection(db.ConnectionString); await probe.OpenAsync();
            await using var update = new SqlCommand("""
                SET LOCK_TIMEOUT 0;
                UPDATE dbo.AssumedBreakConfigurationSnapshot SET PayloadBinary = PayloadBinary;
                """, probe);
            var error = await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal(1222, error.Number); // The selection must retain its snapshot lock until validation completes.
        }
        finally
        {
            await barrier.RollbackAsync();
            await selection;
        }
        Assert.Equal(admission.Identity, (await selection).Value?.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualBreakFromDifferentSourceIsNeverAdmittedEvenWhenIntervalsOverlap(bool reverse)
    {
        await using var db = await CreateAsync();
        var store = new SqlServerAssumedBreakAuthorityStore(db.ConnectionString);
        var other = new AuthorityExclusion("other", Start.AddMinutes(10), Start.AddMinutes(30), AuthorityExclusionClassification.OtherExclusion);
        var actual = new AuthorityExclusion("actual", Start.AddMinutes(20), Start.AddMinutes(40), AuthorityExclusionClassification.ActualBreak);
        var snapshot = Snapshot() with { ExclusionState = ExclusionManifestState.CompleteIntervals,
            Exclusions = reverse ? [actual, other] : [other, actual] };
        var admission = Admission(snapshot);
        await store.PublishSnapshotAsync(snapshot, snapshot.Identity, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAdmissionAsync(admission, admission.Identity, CancellationToken.None).AsTask());
        Assert.Equal(BreakAuthorityReadOutcome.Absent, (await store.ReadAdmissionAsync(admission.Identity, CancellationToken.None)).Outcome);
        Assert.Equal(BreakAuthorityReadOutcome.Found, (await store.ReadSnapshotAsync(snapshot.Identity, CancellationToken.None)).Outcome);
    }

    private static async Task<SqlStartupIsolatedDatabase> CreateAsync()
    {
        var db = await SqlStartupIsolatedDatabase.CreateAsync();
        try
        {
            await using var connection = new SqlConnection(db.ConnectionString); await connection.OpenAsync();
            await new SqlServerMigrationEngine(SqlMigrationCatalog.Load(), new Clock()).ApplyAsync(connection, TimeSpan.FromMinutes(2), CancellationToken.None);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private sealed class Clock : ISqlMigrationUtcClock { public DateTimeOffset UtcNow => Start; }
}
