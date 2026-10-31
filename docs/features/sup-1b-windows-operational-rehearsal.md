# SUP-1B — isolated Windows operational rehearsal (proposal, NOT EXECUTED)

PR #172 documentation-only procedure. Merge and production activation remain **HOLD**. Do not use Gajra, a production backup, production certificates, or a shared development database. Execution requires separate approval of an isolated Windows machine and disposable SQL database. All commands below are source-derived where indicated; environment placeholders must be resolved and reviewed before execution.

## Source anchors

- `tools/FactoryConnect.SecurityAdmin/Program.cs`: `install <approval-reference>`, `bootstrap <approval-reference> <operation-guid> <username> [machine-guid ...]`, `recover <approval-reference> <operation-guid> <username>`; SQL and request key read from `FC_SECURITY_SQL` and `FC_SECURITY_REQUEST_KEY`. Bootstrap/recover require interactive (non-redirected) console and read temporary password without echo. The tool prints serialized result; it does not authenticate the approver.
- `docs/features/sup-1b-security-foundation.md`: SupervisorSecurity defaults disabled; opt-in requires HTTPS CanonicalOrigin, ConnectionString, absolute KeyDirectory, LocalMachine/My CertificateThumbprint with private key, and protected RequestBindingKey (base64 >=32 random bytes). Key ring is certificate-encrypted. Security schema installation is separate from normal migrations.
- `docs/features/sup-1b-p0-hardening.md`: credential/session/reset invalidation and fail-closed login contract.
- `tests/FactoryConnect.Integration.Tests/SupervisorSecurity*SqlTests.cs`: SQL characterization; not a substitute for Windows certificate, least-privilege, or console recovery acceptance.

## Evidence statuses

For **every** case use exactly PASS / FAIL / BLOCKED / NOT EXECUTED. Capture UTC timestamp, case ID, Windows execution identity, SQL principal (where applicable), application subject, command or operation (redacted), expected/actual result, exit code, and relative evidence file path. Never record passwords, connection strings, session cookies, request-binding keys, PFX passwords, or private keys.

## Preparation — read-only source/build checks

On the developer workstation, verify PR #172 HEAD `4009b0b3d39ee438a48acc10c41095a07a91a02b` and SDK `10.0.402`:

```powershell
cd F:\Development\FactoryConnect-SUP1B
git rev-parse HEAD
git status --short
dotnet --version
dotnet build .\FactoryConnect.sln --configuration Release
dotnet test .\tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --list-tests | Select-String 'Security|Recovery|Session|Password'
Get-Content .\tools\FactoryConnect.SecurityAdmin\Program.cs
```

Do not run SQL tests until their fixture connection and database creation/deletion behavior have been verified to target the disposable instance only. After that review, the repository CI category is:

```powershell
dotnet test .\tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --filter 'Category=SqlServerIntegration' --logger 'trx;LogFileName=sup1b-sql.trx' --results-directory .\TestResults\SUP1B
```

## Identities and least-privilege boundary

Provision *separate*, test-only Windows operator, runtime service, recovery operator, independent approver, SQL provisioning principal, and SQL runtime principal. Record actual Windows SIDs and SQL `ORIGINAL_LOGIN()` / `USER_NAME()` in evidence. Provisioning principal alone may create/drop disposable DB and install schema. Runtime principal must not have DDL, database ownership, or server-wide roles. Recovery credential access and console executable access are separately controlled; SQL permissions alone cannot distinguish ordinary security administration from recovery. Before rehearsal, document the exact SQL grants derived from `src/FactoryConnect.Security/security-v1.sql` and `SqlSecurityAuthority.cs`; do not assume generic `db_datawriter` is least privilege.

## Certificate and encrypted-backup cases

KEY-01: provision a disposable LocalMachine/My test certificate with private key; configure certificate thumbprint, key directory, protected request-binding key and HTTPS origin. Start isolated API with security explicitly enabled. Expect successful initialization. Record certificate thumbprint, subject, expiry, key directory ACL (not private key).

KEY-02: persist protected key ring and security state; restart the same authorized runtime identity. Expect protected session/data to remain readable.

KEY-03: remove runtime identity's access to the *test* certificate private key; attempt restart/unprotect. Expect fail closed; record Windows ACL and application error category. Restore ACL; expect recovery. Do not modify production certificates.

KEY-04 (distinct from ACL restore): create encrypted, access-controlled backups of BOTH key-ring directory and certificate **including private key**, and independently protect the request-binding key. On a fresh disposable restore target, restore certificate/private key and key-ring from backup, grant only authorized runtime identity, and verify decryption of pre-backup protected data. A new key ring or newly generated certificate does **not** pass. Record backup/restore hashes and access-control evidence, never backup secrets in the evidence bundle.

## SQL cases

SQL-01: as restricted runtime principal, execute only documented supported operations against disposable security state. Expect success.

SQL-02: as runtime principal, attempt a disposable schema alteration (e.g. `ALTER TABLE dbo.FactoryConnectSecurityAudit ADD RehearsalShouldFail bit NULL`). Expect permission denied; verify column absent. **Do not run this probe under a privileged identity.**

SQL-03: as runtime principal, attempt unauthorized database operations (e.g. `CREATE TABLE dbo.RehearsalShouldFail (Id int)`). Expect permission denied and no object created. Also verify no `db_owner`, `db_ddladmin`, or server-admin membership.

SQL-04: repeat supported operations after denied probes; expect no degradation. Capture permissions, SQL error numbers, and resulting state.

## Trusted-console administrator recovery

Source-derived invocation **shape** (illustrative placeholders; do not run until identity/connection/approval are provisioned):

```powershell
# In an approved interactive Windows console only:
# FC_SECURITY_SQL and FC_SECURITY_REQUEST_KEY must already be securely set for this process.
dotnet run --project .\tools\FactoryConnect.SecurityAdmin\FactoryConnect.SecurityAdmin.csproj --configuration Release --no-build -- install <approval-reference>
dotnet run --project .\tools\FactoryConnect.SecurityAdmin\FactoryConnect.SecurityAdmin.csproj --configuration Release --no-build -- bootstrap <approval-reference> <operation-guid> <username> <machine-guid>
dotnet run --project .\tools\FactoryConnect.SecurityAdmin\FactoryConnect.SecurityAdmin.csproj --configuration Release --no-build -- recover <approval-reference> <operation-guid> <existing-username>
```

ADM-01: bootstrap disposable test account, then recover that **existing** subject through authorized trusted console. Expect no new subject or grant.

ADM-02: record separately (a) recovered application subject GUID/username, (b) actual Windows and SQL execution identities, and (c) independently approving person and external approval evidence. The CLI's approval-reference argument is **not** two-person enforcement and does not itself establish operator/approver identity. Correlate operation GUID and durable application audit.

ADM-03: pre-create test sessions and reset tokens; after recovery expect old sessions/reset authority invalidated, according to current implementation.

ADM-04: use recovered temporary credential in isolated API; expect forced password change before administrative access.

ADM-05: compare roles, memberships, scoped grants and expiry/effective boundaries before/after recovery; expect unchanged grants.

ADM-06a: deny Windows executable/console/credential access to unauthorized operator; expect OS/tool-access denial. Record Windows security/access evidence; do **not** claim an application audit entry.

ADM-06b: for a request reaching application authorization, expect application denial and relevant audit where implemented. Distinguish this from OS denial; no invented application-side two-person enforcement.

## Acceptance and sign-off

For each KEY-01..04, SQL-01..04, ADM-01..05, ADM-06a/b, fill: `Case | Status | Expected | Actual | Evidence path | Reviewer`. A missing proof is BLOCKED or NOT EXECUTED, never PASS. Capture process identity, certificate/ACL metadata, SQL principal/grants, redacted CLI output, application audit correlation, and before/after grant diff. Reviewers independently approve evidence and any operational credential-control gaps. A PASS on Linux CI or SQL tests does not close Windows gates.

**No commands in this document have been executed by this documentation update. No production or database changes are authorized. PR #172 remains draft, merge/production HOLD.**
