# Q2B.3C — One-attempt extraction authorization record

Decision: NO-GO / NOT AUTHORIZED. Documentation-only review candidate.
This record prepares approval; it does not permit a database connection or extraction.

## Frozen repository baseline

- Tooling: PR #141 squash b857fb0d8df32c6ffdfcaebcb369c0eebb5a5b02, validated feature head 47a3fb76961064a91c603e6d5554ca303ebbe445. General CI and offline exporter workflow passed.
- Safety review: PR #142 squash 93bc03c582375c855f1aadc53a6d95afea852a2c. Required CI passed at feature head 3ff72f4dd97dd7538af0f805fff8f6f432a8057b.
- Use checkout 93bc03c582375c855f1aadc53a6d95afea852a2c for execution-artifact preparation. The “UNSET pending merge” checklist text in the preceding review is superseded for commit identity only; local artifact hashes remain unverified.
- SQL: scripts/diagnostics/gg003-historical-inventory/Q2B-revision-534-lineage-review.sql; UTF-8 without BOM, LF, 7694 bytes; SHA-256 DBB7AF00AA97241F789D982B43A5740C774A79973360BCB6BA66D51D8CAC7719.
- Reader project declares Microsoft.Data.SqlClient 7.1.1 and direct System.Security.Cryptography.Pkcs 9.0.21. Retain actual resolved transitive dependency inventory and build hashes; declarations are not local binary verification.

## Exact target and scope

Server property: `DESKTOP-1PJK2OB\FCINVENTORY`.
Database: FactoryConnect_GG003_IMP1C_INVENTORY_20261020.
Machine: 677b030b-1ede-4a67-82ae-6a494791357f.
Aggregation processor row 1; metric-input stream row 1; committed watermark 534.
One full contribution-ledger extraction, all admitted keys/dates through that watermark, complete three-part fact references. No latest-cut substitution, generic six-set script, date filtering or automatic retries.

Seven ordered sets: Q2B-target, Q2B-cut, Q2B-contribution-boundary, Q2B-fact-membership, Q2B-duration-operands, Q2B-coverage, Q2B-end-target.
Singleton sets: target, cut, boundary, coverage, end-target. Full membership counts must reconcile. Anomaly counts overlap.

## Fields required before approval

| Field | Value / evidence |
| --- | --- |
| Authorization ID / approver / approval timestamp / expiry | UNSET |
| Operator / workstation / non-elevated Windows identity | UNSET |
| Exact expected ORIGINAL_LOGIN / database principal | UNSET |
| Backup provenance and immutable READ_ONLY restore record | Bind existing approved record; reference/hash UNSET |
| Administrator grant/role/visibility attestation | Existing record reference and continued applicability UNSET |
| SQL local hash, source-file hashes, reader binary and dependencies | UNVERIFIED; attach exact-checkout report and build inventory |
| Local working directory, spool, evidence, envelope, stdout/stderr paths | UNSET; absolute dedicated local paths, ownership/ACLs, no reparse/network/cloud-sync destination |
| Intended-volume offline publication validation / free space | Evidence reference and combined disk budget UNSET |
| Exact reader command and offline verifier command | UNSET; review both complete invocations before approval |
| Process supervisor deadline / termination / cleanup allowance | UNSET; client timeout is not a strict wall-time or SQL I/O limit |
| Evidence custodian / reviewer / retention location | UNSET |

Resource envelope: connect timeout 15 seconds; command timeout and reader cancellation budget 60 seconds; 1000 rows per result set; SQL membership guard 1000. Spool maximum 10485760 bytes; final data plus manifest maximum 10485760 bytes separately. Budget staging, receipts and logs in addition. Freeze an overall process and local-disk limit before approval.

No executable command is supplied while identity/path fields remain unknown. Do not infer them from current workstation defaults or previous connection aliases.
The eventual envelope must retain authorized=false until explicit approval and bind exact server/database, SQL hash, authorization ID, expected login and frozen resource limits. The technical authorized flag alone cannot grant permission.

## Approval sequence

1. Complete the unresolved fields using retained evidence and offline artifact/path inspection. No new SQL query or setup is authorized by this document.
2. Review exact commands, artifact identities, restricted principal and operational supervision against Q2B3-extraction-readiness-review.md.
3. Obtain explicit approval for one bounded read-only attempt with a defined time window and fresh output paths. Record GO, approver and all bound fields; otherwise remain NO-GO.
4. Only after that approval, the designated operator may run the approved capture once. Record exit codes and errors. No automatic retry.
5. Independently verify terminal completion, sidecar hash, schemas, identities and row counts before atomic evidence publication. Failure means incomplete capture, never successful evidence.
6. Preserve successful spool/sidecar, manifests, logs, SQL/source/build identities and hashes. Review residual incomplete artifacts; do not fabricate receipts or steal stale locks.

## Interpretation and recovery boundaries

complete=true establishes complete typed-reader capture only.
Q2B.4 must reconcile original MetricValue strings with exact scope and ledger membership before arithmetic interpretation; unresolved references, conflicting identities or duplicates block it.
Distinguish incorrect aggregation, valid contributions misinterpreted by reporting, schedule/policy mismatch, and insufficient evidence. Running outside plan alone proves none of these defects.
Approximately 2683.61 / 2452.01 seconds and 109.45% remain prior RCA diagnostics, not newly extracted evidence.

No clamping, raw-observation changes, SQL repair, migration, index creation, application startup, reporting replay, production connection or deployment is authorized.
Historical numeric feasibility remains NotEstablished. IMP-1 remains open. The next decision is approval of a completed execution record, not implementation of an assumed fix.
