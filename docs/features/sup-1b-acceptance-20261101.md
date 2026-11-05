# SUP-1B — acceptance reconciliation, 1 November 2026

PR #172 remains draft. Merge and production activation remain HOLD.
Reviewed branch head before this documentation update: `b948d388d504d12a719cb85aba47b8123fc01789`.
Validated implementation/test head: `4009b0b3d39ee438a48acc10c41095a07a91a02b`.
The subsequent `b948d388` change adds only the Windows rehearsal procedure.

## Provenance and independent gates

| Gate | Disposition | Evidence and limits |
| --- | --- | --- |
| Code-level P0 regression packet | PASS | GitHub CI run 41227602399 and retained sql-integration-regression-evidence: 971 passed, 3 skipped; all 29 SupervisorSecuritySqlTests individually verified. Includes hash upgrade/session preservation and reset rollback/lost acknowledgement. |
| Windows Release build and LocalDB regressions | PASS — operator supplied | User reports Windows build and independently inspected TRX: 971 passed, 0 failed, 3 not executed; all 29 security cases passed. These are distinct from the CI environment. |
| SQL-01/04 restricted runtime operation and continued operation | PASS — operator supplied, exercised scope only | Authenticated administrator password rotation, previous-session revocation, replacement-password login, persisted operation/result/audit and account state inspected. Not independent access to the Windows host by this reviewer. |
| SQL-02/03 permission-denial probes | PASS — operator supplied | User reports restricted-permission checks passed. Retain actual principal/grants/error evidence in the private rehearsal archive; no claim that all SQL or recovery separation is enforced by database grants. |
| Certificate/key backup and clean restore | NOT VERIFIED in this record | Requires proof that pre-backup protected data is readable after clean restore of the original certificate/private key and key ring, with correct ACLs. ACL restoration alone does not establish backup recovery. |
| Trusted-console recovery | PARTIAL — operator supplied | Recovery administration revision 2 followed by password rotation at revision 3 is reported. Complete ADM coverage still needs correlated execution identities, independent approval, old reset invalidation, grant comparison and unauthorized-access evidence. Do not repeat completed recovery merely to collect duplicate outcomes. |
| Rehearsal secret retirement | PENDING | User reports protected CMS package contains obsolete temporary credential, replacement credential and request-binding key. Retirement has not been confirmed. |
| Final SUP-1B security/merge decision | HOLD | Code regression success and scoped runtime evidence do not automatically close all operational or review criteria. |
| Production activation | HOLD | No production security/schema/configuration deployment authorized. Accepted Gajra baseline remains 85f56f32 as reported by the operator. |

The three unexecuted tests are Gg003BoundedContainmentCatchupAcceptanceTests,
Gg003RestoredHistoricalAcceptanceTests, and
Gg003RestoredContainmentRuntimeAcceptanceTests. Their separate historical acceptance remains open;
none is counted as a security pass.

## Restricted-runtime durable result

The operator reports the following readback from the isolated Windows/SQL rehearsal:

- Operation ID: `44b6fa34-bdc6-492a-8490-194a1ff92c9e`.
- Stored outcome: `ChangedAndSessionsRevoked`, with matching durable audit.
- Administration revision: recovery 2, then password change 3.
- Physical SQL security-state revision: 11, independent of AdministrationRevision.
- Previous session rejected; replacement-password login succeeded.
- Must-change-password requirement cleared; account enabled and unlocked.

This closes SQL-01/04 only for the operations exercised. No further recovery or password-change
execution is required for this accepted result.

The user reports that two uploaded TRX files are byte-identical, with SHA-256
`EF9A6316E66D1AE4D802D92264DB7CD348FCA7D833CA423941A42A05F867CF2E`.
This checksum is operator-supplied provenance for the Windows artifacts; it is not a claim
that these are byte-identical to the GitHub CI artifact. Store redacted evidence references
privately; do not commit credential-bearing packages, private keys, cookies or connection strings.

## Cleanup and remaining review

1. Inventory the protected CMS package and any rehearsal-only credential copies. Record custodians,
   access controls and disposal confirmation without recording secrets.
2. Confirm whether the disposable authority will remain in use. The request-binding key must remain
   recoverable while retained operation bindings need it; do not destroy the sole required copy.
   If the disposable environment is retired, retire its credentials and key material through the
   approved procedure. This document neither executes cleanup nor guarantees physical media erasure.
3. Correlate remaining KEY and ADM evidence against
   [the Windows rehearsal procedure](sup-1b-windows-operational-rehearsal.md).
   OS/tool-access denial is not an application audit record; an approval reference is not proof of
   independent two-person authorization.
4. Obtain final custom-adapter/security risk disposition, including privileged hash work under the
   administration gate, pilot state scaling, rate-limit exhaustion, audit/retention/tamper controls
   and recovery credential custody. Track deferred items explicitly rather than inferring acceptance.
5. Review checks for the final documentation head once the owner reports completion. No CI polling.
   A separate explicit decision is required to mark ready/merge; production enablement remains separate.

Default-disabled security, unprotected existing reporting routes, unmapped reset/SMTP integration,
and absent workload authentication remain unchanged boundaries. SUP-1C and SUP-2 are separate scopes.
This reconciliation changes documentation only; no code, test, SQL grants, credentials, host settings,
recovery operation, process or Gajra system is modified.
