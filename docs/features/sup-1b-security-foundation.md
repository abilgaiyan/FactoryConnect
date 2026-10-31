# SUP-1B — local security foundation candidate

31 October 2026; base `85f56f32880dd1b47be1a605c85da2a86d4fc304`; roadmap #171/#164.
Draft implementation for review, not production acceptance or deployment instructions.

## Delivered boundary

A separate `FactoryConnect.Security` library owns SQL-backed local accounts, role grants,
opaque server-side sessions, privileged command registration/results and security audit.
It uses ASP.NET Core Identity's `IdentityUser<Guid>` and versioned `PasswordHasher<T>`;
it does **not** use EF Identity tables, UserManager/SignInManager or claim that framework
registration alone enforces FactoryConnect authorization. Review the custom SQL/session
adapter explicitly. Password hashing is framework-owned; no custom password cryptography.

Initial account and subsequent users receive 15-minute temporary credentials and must change
them before administrative access. Changing/resetting credentials revokes sessions. Usernames
are unique normalized ASCII names, with immutable server-generated GUID principals; email
login and email verification are intentionally not available. No email-domain assumption.

Only reports.view (one exact MachineId) and security users/roles/access/audit actions are in
the application-owned catalogue. No declaration, approval, qualification, publication,
configuration activation or historical correction permissions are enabled. Bootstrap grants
administrative actions plus report grants for explicitly supplied machines, never wildcard
machine access. No role inheritance; no dynamic site/line scope. Role names confer nothing.
Role editing and membership assignment check the grantor's currently delegable grants,
including effective/expiry boundaries and removed scopes. A principal cannot edit its own
role or membership. Disabling users and changing memberships revoke their sessions.

## Database and transaction contract

The schema is **separately installed**, not added to production migrations 001–023 and not
created by API startup. Tables: dbo.FactoryConnectSecurityState (v1, optimistic global revision,
JSON membership snapshot), dbo.FactoryConnectSecurityOperation (full request bytes, stable
allocated ID, immutable recorded result), dbo.FactoryConnectSecurityAudit (append-only app
behavior). The pilot snapshot serializes security writes and is a bounded-pilot design,
not a scalability claim. Runtime requires version 1 and fails when the authority is absent.
Use the same database/connection authority for future source work if using the transaction
callback; do not resolve evidence on an independent connection. No new Supervisor source
write is connected in this PR. Existing schema migration/startup behavior is unchanged.

Lock resource: literal ASCII `FactoryConnect.Security.Eligibility.v1`, SQL database scope,
DbPrincipal=`public`, LockOwner=`Transaction`, 5-second timeout; every negative return fails.
Eligibility changes/administrative commands acquire Exclusive. Protected callbacks and
session activity take Shared. State mutations additionally take UPDLOCK/HOLDLOCK. Order:
eligibility gate → state row → operation key → future consumer thread/source locks. No upgrades,
network calls, browser editing time or recursive security calls while holding the gate.
A shared protected operation may finish before an exclusive revocation can commit; subsequent
operations observe revocation. Source callbacks receive the same connection and transaction.
Denial rolls back consumer writes before a separate short denial-audit transaction.

Privileged commands register under (subject GUID, operation GUID) in a separately committed,
authorized transaction. Execution reauthorizes before inspecting the request/result/target,
then atomically applies state, audit and recorded outcome. Identical authorized retries return
historical result IDs/revision; changed bytes conflict. Stale revision and deterministic
restrictions are retained; infrastructure failures roll back execution, leaving registration.
Principal IDs allocated during registration survive retries. Unknown commit outcomes require
the same operation/request, never a replacement ID. No deletion/expiry of operation rows exists.
Audit retention is independent of deduplication; no retention cleanup is implemented.

Request encoding v1: prefix `factoryconnect-security-command:v1\n`, UTF-8 System.Text.Json
record property order, explicit nulls, original text and ordered arrays. Revisions cross HTTP
as canonical nonnegative Int64 decimal strings (no whitespace/sign/leading zero/overflow).
The password field is replaced by uppercase HMAC-SHA256 using a persistent protected random
request-binding key, not a plaintext password or unkeyed password fingerprint. Store full bytes,
not hash equality. That key must not be replaced while old operation bindings remain live.
Registration/pending result is not approval or successful execution. Public failures contain
safe categories and diagnostic IDs; no SQL errors, hashes, tokens or request body disclosure.

Authentication exchanges themselves are separate from privileged command idempotency: a fresh
login attempt may create a fresh session; failed attempts and global/account throttling are
recorded. Login is not advertised as exactly-once. Review this exception explicitly against
SUP-1A's final security-operation policy before acceptance.

## HTTP/security configuration

Default absent/false SupervisorSecurity:Enabled maps no new endpoints. Opt-in requires
CanonicalOrigin (one HTTPS origin), explicit ConnectionString, absolute KeyDirectory,
CertificateThumbprint (one valid LocalMachine/My private-key certificate), and RequestBindingKey
(base64, >=32 random bytes). No developer bypass or sample administrator. Data Protection keys
persist and are certificate-encrypted; key-directory/private-key ACLs, key backup and certificate
rotation need operational acceptance. Request-binding key is independently protected/backed up.

`/api/security/v1`: csrf, login, session, administration, commands, session-actions, audit.
Cookie is __Host-, Secure, HttpOnly, SameSite=Strict and protected with Data Protection.
Writes require exact Origin, antiforgery token, HTTPS and canonical Host; forwarded requests
are rejected. Proxy support is not assumed. Background GET reads do not touch idle time;
explicit interactive action does not extend eight-hour absolute life. Idle limit 30 minutes.
Five failed account attempts lock for 15 minutes; global 30 attempts/minute bounds password
hashing including unknown names. No password/token logging. The framework dummy-hash path
reduces obvious unknown-account timing differences; no constant-time HTTP claim.

These endpoints **do not secure existing reporting routes**. Dashboard proxy/session propagation,
production login/Users/Roles/Machine Access UI and secured Supervisor route are SUP-1C/SUP-2.
Do not enable this API and expose an existing unprotected reporting path as a secured product.
Existing reporting and dev-only Supervisor gates remain unchanged. No workload can sign in as a
service through human accounts; workload authentication/publication integration remains absent.

## Reset and offline recovery

Reset service accepts only an authorized administrator, verified stored destination, persistent
Data Protection protector and explicitly configured IPasswordResetDelivery. It persists a
protected outbox token before external delivery; identical issuance retry resends the same link.
Token consumption is single-use, expires at exactly 15 minutes, and revokes all subject sessions.
SMTP, verification, email login and reset HTTP endpoints are **not mapped**: fail closed until
an approved adapter and workflow are reviewed. Delivery cannot atomically commit with SQL or
retract bytes; repeated delivery may occur, repeated password mutation must not.

FactoryConnect.SecurityAdmin is an explicit trusted-console tool: install, bootstrap, recover.
It never runs from API/startup/deployment, has no HTTP endpoint, no default password and reads
temporary passwords with hidden console input. Connection and persistent request key come from
protected environment settings, not command-line credentials. Bootstrap is one-time; recovery
changes only an existing principal's credentials/eligibility, never silently grants new roles.
Approval reference and stable operation participate in content matching. Operator identity,
two-person authorization, executable ACLs, credential handoff and recovery records are deployment
procedure gates, not claims that arbitrary reference text cryptographically proves approval.
Audit runtime SQL principal is retained; do not grant the web service DDL/bootstrap execution.

## Acceptance and exclusions

Build before tests. Non-SQL tests cover expiry, permission/scope/delegation, canonical encoding,
framework hashing, fail-closed options, transport/origin/CSRF guards and key persistence.
SQL tests target disposable test databases only: bootstrap/restart/retries, stale edits,
self-escalation, revoked retrieval, rollback after audit failure, callback rollback on denial,
shared/exclusive revocation ordering and reset expiry/single-use/session revocation.
No SQL Server or Gajra connection was used locally; SQL cases are authored, not locally passed.
Key-persistence test uses a synthetic key directory; it does not prove Windows certificate ACLs.
Full revocation wait-state observation, cancellation/deadlock/commit-ack transport injection,
last-admin recovery drills, tamper/retention policy, SMTP delivery and Windows LAN HTTPS remain
acceptance gaps. Do not mark SUP-1B complete or enable production from build/ordinary tests alone.

No merge/deployment/production schema installation, host mapping, certificate/firewall change,
Supervisor write, quantity qualification, OEE recomputation or historical replay is authorized.

## Local validation record

.NET SDK 10.0.100/Linux, DOTNET_PROCESSOR_COUNT=2. Full solution Release build PASS,
zero compiler/analyzer warnings or errors. Build preceded ordinary .NET tests:
Core 896, Modbus 1, MTConnect 61, Edge 242, Integration 886, Dashboard host 103,
HistoricalRecovery 36: **2,225 passed, zero failed/skipped** across final suite results.
Integration includes 24 new non-SQL security cases. Seven new SQL cases are authored,
not executed locally; normal CI's SQL category includes them.

This container lacks the legacy Asia/Calcutta zoneinfo alias. Eight existing Edge cases
failed for that environmental reason, then all 242 passed with a temporary TZDIR overlay
mapping Calcutta to the existing Kolkata data. No repository timezone/schedule change.
Tests run with single-node MSBuild because sandbox named-pipe multiprocessing is unavailable.
A pre-existing CA1869 in ConfigurationPreflight was fixed by caching identical JSON options;
no output-format behavior changed. Local Node 24.22.1 differs from pin 24.14.1; solution's
frontend production build completed, but no new frontend feature is included.

No SQL, production, restored backup or factory endpoint was contacted. CI is not polled;
keep draft until independently reviewed and SQL/concurrency and operational gates pass.
