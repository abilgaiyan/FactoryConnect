# IMP-1C query packet — Q0/Q1 preflight and schema discovery

Status: draft for review, not execution authorization.
Baseline: architecture/specification main 072d935137193f2d315aa6c7629630c703f0013a.
Procedure dependency: PR #138 head 301adf47eee881aea5d009ea169325fae9217fc7 (accepted, merge pending when prepared).

This first packet contains only:
- Q0-target-permission-preflight.sql: target guards and identity/permission metadata discovery.
- Q1-schema-discovery.sql: target guards and catalog metadata for evidence families and selected key/permission candidates.
No source table data, historical reconstruction, Availability calculation, application initialization or write statement is included.
Neither file has been executed or syntax-tested against SQL Server. Review includes syntax/schema/version compatibility before execution. SELECT metadata still requires explicit authorization to connect.

## Execution authorization record — deliberately unfilled

| Field | Value to supply and approve |
| --- | --- |
| Disposable SQL instance / exact ServerName | NOT PROVIDED |
| Database / pre-existing ONLINE READ_ONLY state | NOT PROVIDED |
| Backup provenance/hash and immutable custody | NOT PROVIDED |
| Inventory operator / dedicated login / run ID | NOT PROVIDED |
| Procedure and packet commit/hash | Freeze after review |
| Q0 allowed metadata reads and output destination | NOT AUTHORIZED |
| Administrator grant/role/module attestation | NOT PROVIDED |
| Q1 catalog discovery scope | NOT AUTHORIZED |
| Machine/date/cut source-data scope | GG-003 candidate dates only; exact cut list not selected |
| Execution authorization | NOT GRANTED |

No host/database from earlier chats is silently substituted. No credentials belong in these files.
Restore/provisioning, changing READ_ONLY state and login grants are outside the packet.

## Review sequence

1. Review and approve the procedure and Q0 code plus actual disposable target record before connecting.
2. Replace only the two approved identifier placeholders in a reviewed local derivative; record its content hash. Server comparison uses SERVERPROPERTY('ServerName'), not a connection alias. Guard mismatch stops; do not remove guards.
3. Q0 requires ONLINE READ_ONLY and rejects sysadmin/db_owner/unknown role results. It returns visible effective permissions/tokens/role links; these are not a complete privilege proof.
4. Administrator attestation must establish nested/inherited/server/object grants, ownership, impersonation and executable module paths. Reject excessive write/DDL/control/impersonation/privileged execution capability. Limited visibility or inability to attest leaves preflight incomplete.
5. Review Q0 outputs before approving Q1. Q1 is not automatically chained. It repeats target checks and reads catalog metadata only.
6. Use Q1 schema to prepare the source-data packet Q2+ separately. This PR does not authorize or implement those queries. Migration history contents also remain a later reviewed SELECT; catalog discovery alone does not establish applied migrations.
7. Record target/capture identity with each result; keep original output, sanitized derivative identity and complete result-set inventory. No row truncation without an explicit NotEstablished limitation.

Use direct SQL text through an approved client with no startup scripts, mutating service bootstrap or automatic schema initialization.
No SQLCMD connection directives, USE statements, EXEC, DML, DDL or write-permission probes are present. DECLARE affects session variables only; THROW enforces guards.
ApplicationIntent=ReadOnly is not a replacement for READ_ONLY target state and restricted credentials. Catalog queries and token visibility cannot establish all access paths; the attestation remains required.
Q1 name-pattern discovery is a candidate inventory, not a universal archive search or proof of absence. Object permission checks cover listed candidates only; the final Q2+ table allowlist must be independently reviewed in full.

## Q2+ work intentionally pending restored schema

The next reviewed packet must select exact processor/stream/revisions and complete incorporated fact membership; resolve output payloads and source activity identities; inventory acquisition/raw and exact coverage references; capture artifact provenance separately.
It must bind reviewed target/machine/cut parameters, use stable paging and expected/returned counts, retain exact bytes/typed positions, and fail honestly on missing schema/dependencies. No speculative table repair or current-schedule fallback.
IMP-1 historical data execution has not started; numeric feasibility remains NotEstablished. Packet preparation is not runtime implementation, migration, replay or deployment authorization.
