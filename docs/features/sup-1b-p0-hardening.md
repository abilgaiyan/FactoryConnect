# SUP-1B P0 hardening candidate

Base PR #172 head `0b6fa3c811a9fa5e4738223a8b025f8e0eeb9ca9`. Implementation/review only.
PR stays draft; merge, production activation and SUP-1 acceptance remain on HOLD.
This document supersedes the initial candidate's login/retry/revision limitations where stated.

## Reset credentials and result access

Password changes, offline recovery and account disable/enable invalidate every outstanding
reset and revoke subject sessions in the same transaction. AuthenticationGeneration advances.
A successful reset consumes its token and invalidates other tokens. Its own consumed token
can retrieve the identical result only within its original expiry, provided it has not
subsequently been invalidated by another credential change. It cannot execute a different
operation. Expired, disabled-account or invalidated credentials never authorize token retrieval.

GET `/api/security/v1/password-reset-results/{operationId}` requires the secure human session
and independent `security.password-reset.result.view` grant before any operation lookup.
It reads only the caller's subject namespace and only password.reset.consume actions.
Wrong subject/action is NotFound; pending is explicit; completed result is labelled historical.
No reset token, delivery ciphertext, request bytes or password fingerprint is returned.
No registration or mutation replay occurs. Existing accounts do not acquire a newly defined
permission implicitly. No anonymous reset-result route exists. Actual reset delivery/consumption
HTTP endpoints and SMTP integration remain disabled/unmapped.

## Login contract and locks

Login is explicitly a non-idempotent credential exchange, not a privileged command.
A retry authenticates afresh and may issue another session; it cannot retrieve an earlier
session token. Lost acknowledgement does not justify claiming rollback. Limits: 30 reserved
attempts/global minute, eight concurrently outstanding verification tickets (two-minute expiry),
five active sessions/account. Additional sessions are denied, not silently created. Completed
or expired verification tickets are pruned; no security-operation idempotency records are pruned.

1. A short Exclusive-gate transaction reserves rate capacity and an internal one-use attempt.
   It captures an owned account/password-hash/security-stamp/authentication-generation snapshot.
2. No SQL connection, transaction, eligibility lock or state row lock survives into password
   verification or optional rehash computation. Unknown/ineligible names use framework dummy hashing.
3. Another short Exclusive transaction rechecks the exact attempt, expiry, identity, hash, stamp,
   generation, disablement, temporary-credential deadline and lockout before session issuance.
   Credential change/recovery/disable/re-enable/lockout invalidate the in-flight snapshot.
   Explicit global session revocation and role/membership changes also advance the generation.
   No cached successful verification can be rebased onto a newer credential generation.

Five qualifying failed attempts establish lockout atomically. Concurrent failed verification
cannot race past an already committed lockout. Cancelled/abandoned verification reserves its
bounded slot until expiry; it never creates a session. This is admission control, not a complete
network abuse defense: a global limit can itself be exhausted. External rate limiting and
session/audit archival remain P1 operational design items.

Privileged user creation/password change/recovery still compute new password hashes under an
administrative gate. They are not the public login path, but their contention remains a P1
measurement/review item. No production latency guarantee follows from this change.

## Revision domains

The SQL state's Revision remains a physical compare-and-swap revision for every state update.
Payload AdministrationRevision is independently incremented for administration/credential changes.
Login tickets, lockout bookkeeping, session creation and explicit interactive touches update
physical storage without changing the administration concurrency token. Authorization always
reads current state under the same gate; revision separation never caches eligibility.
Credential-generation changes are checked independently of both revision domains.

New payload fields default safely for the unactivated draft v1 schema. Old command content is
never rewritten; completed operation results preserve their original recorded revision domain.
Old pending commands using a physical revision may conflict rather than silently rebase. This
is not a production schema migration or approval to upgrade any deployed security authority.

## Correctness evidence (authored SQL, execution pending)

Tests use disposable databases and internal friend-test hooks; no HTTP/config hook exists.
- Password change and recovery invalidate multiple reset tokens and existing sessions.
- A paused verification permits a protected read; password change, recovery, disablement and
  lockout before issuance each reject the in-flight attempt.
- Login reservations and active sessions are bounded; interactive touches do not invalidate
  an administration command, while actual administration changes do.
- Expired token retrieval denies; current-session/action grant returns the exact historical
  result; removing the retrieval grant denies before lookup.
- Application-lock DMV observation establishes a waiting Exclusive revocation behind a Shared
  operation. After revocation commits, a later protected callback never executes.
- Cancellation after writes/before commit rolls back execution, preserves registration and allows
  same-request retry. An injected lost acknowledgement after real commit returns unknown; retry
  returns the stored result without another mutation.
- Denial-audit failure is explicitly observable as SecurityDenialAuditException/503-safe category,
  never successful access; consumer DML has already rolled back.
- A real SQL deadlock cycle on synthetic rows forces the command transaction to be the low-priority
  victim; registration survives and identical retry can succeed. No isolation weakening/retry bypass.

Timeouts bound test failure/hangs. No sleeps or elapsed-time correctness thresholds establish ordering.
Read historical-result authorization and all supported eligibility writers use the same authority.

## Performance evidence (separate from correctness)

SQL characterization records three post-warmup samples of legacy hash-under-exclusive transaction
cost and complete candidate login cost. They have different scopes, so the output explicitly does
not assert a speedup. A separate coordinated experiment records protected-read latency while
legacy verification holds its gate versus candidate verification outside its gate. Those times
include test barriers/DMV observation and are not representative production latency.
No elapsed threshold is an integrity assertion. CI TRX retention includes test output for review.
SQL timings were not collected locally; never replace missing measurements with invented figures.

## Three skipped SQL tests: classification and disposition

| Test | Reason | Disposition |
|---|---|---|
| Gg003RestoredHistoricalAcceptanceTests.RestoredShift534ReturnsUnavailableWithOriginalWitness | FACTORYCONNECT_GG003_RESTORED_CONNECTION_STRING absent; requires named developer LocalDB restore | NOT EXECUTED. Separate read-only historical witness acceptance remains open |
| Gg003RestoredContainmentRuntimeAcceptanceTests.ActualRuntimePublishes534Preserves380WitnessAndVerifiedNestedOeeThenRestartsAndReplays | Requires Windows plus FACTORYCONNECT_GG003_DISPOSABLE_CONNECTION_STRING and explicit disposable replay clone | NOT EXECUTED. Separate historical write/replay acceptance; never run against original/production |
| Gg003BoundedContainmentCatchupAcceptanceTests.ResumeThroughBoundedHistoricalCutAndRecordFirstFailure | Requires Windows plus FACTORYCONNECT_GG003_CATCHUP_CONNECTION_STRING and bounded disposable catch-up clone | NOT EXECUTED. Separate catch-up acceptance remains open |

These are pre-existing GG-003 restored-history tests, not skipped SUP-1B security cases. Their
runtime guards remain unchanged. CI's former 949 PASS/3 SKIP is historical baseline evidence;
new hardening results must be reviewed at their exact new head. No restored replay is authorized.

## Remaining acceptance

Independent custom-adapter review, new SQL execution/TRX evidence, actual measured contention,
Windows key/certificate/backup recovery, least-privilege SQL, administrator recovery drills,
network abuse policy and audit retention/tamper controls remain open. The new self-result grant
is not permission to read other users' security operations. Existing reporting endpoints and
Supervisor UI are not promoted or secured by this hardening packet; SUP-2 remains separate.

## Local validation for this packet

Integration-project Release build PASS (SDK 10.0.100/Linux), zero warnings/errors. Build
preceded **896/896 non-SQL integration tests**, zero failures/skips. SQL test discovery finds
**26 security cases (19 added by this packet)** under Category=SqlServerIntegration. Discovery
is not execution: these cases and all SQL/performance measurements await CI and review.
CI YAML parses and whitespace checks pass. SQL test output/TRX is retained as
`sql-integration-regression-evidence` for independent correctness/measurement inspection.
No SQL Server, factory endpoint, restored database or production service was contacted locally.
