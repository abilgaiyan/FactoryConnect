# Q2B.3A/B — Revision-534 extraction readiness review

Status: documentation-only review candidate. Q2B.3C execution authorization is NOT granted.
This document does not connect to SQL, restore a backup, run the reader, or authorize extraction.
Baseline: PR #140 squash commit 733ce1e0bead9bc8a0952cc307aa8843bcd98b57.
Reader/tooling inspected at PR #141 reconciled head 47a3fb76961064a91c603e6d5554ca303ebbe445; its 15 file blobs are identical to the previously green head 8277c357cfdf2838dcd879d9200f737d9c89b507.
Record the eventual #141 squash commit and its required CI before freezing an execution record. Branch names alone are not execution identities.

## A — Safety envelope assessment

The tooling supports a narrowly bounded extraction candidate. The operational envelope is NOT ready for execution until the unresolved fields in B are independently verified and Q2B.3C explicitly approves them.
User-reported offline evidence: 30/30 tests, .NET reader build, fresh Windows checkout validation. GitHub CI and offline exporter workflow passed at 8277c357. These establish offline tooling behavior, not live provider metadata, genuine network cancellation, historical operands or authority.

| Requirement | Implementation inspected | Admission / remaining evidence |
| --- | --- | --- |
| Frozen SQL | Reader hashes original bytes before connecting; exporter checks same hash | Canonical UTF-8/no BOM/LF: 7694 bytes, SHA-256 DBB7AF00AA97241F789D982B43A5740C774A79973360BCB6BA66D51D8CAC7719; Git blob e51135ed263618cbc6a80887f7a49661dbf156cd. No parameter edits |
| Exact restore | Hardcoded server/database and matching envelope; SQL requires ONLINE/READ_ONLY at entry, target/end sets retained | Reconfirm approved existing immutable copy/backup provenance without repeating installation or Q0/Q1 |
| Restricted reader | Windows integrated security only, no pooling; SQL rejects sysadmin/db_owner including indeterminate checks | Actual ORIGINAL_LOGIN and database principal, administrator-attested grants/roles and complete scoped visibility must be frozen. Non-owner alone is not proof of least privilege |
| Exact cut | SQL binds machine, processor, stream and committed watermark | Processor row 1, stream row 1, cut 534; ledger admission bounded by watermark, full three-part fact join |
| Seven-set contract | Reader bounds set count/labels; offline verifier checks ordered schema, identities, singleton sets and count reconciliation | Any mismatch prevents final evidence publication; first live provider metadata remains unobserved |
| Precision | SqlDecimal text and datetimeoffset(7) strings; raw schema metadata retained | No float conversion or text-table export; preserve exact values and original payload bytes |
| Command completion | Drain through NextResult(false), dispose reader/command/connection, flush spool, terminal and full-spool completion sidecar | Nonzero exit, absent/mismatched receipt or delayed error invalidates capture; sidecar is a content-binding receipt, not an independent signature |
| Resource limits | ConnectTimeout 15 seconds; command timeout 60; cancellation token 60; 1000 rows per set; spool at most 10485760 bytes | These are client controls, not SQL CPU/I/O or strict process-wall-time guarantees. Bound finalization/process supervision and local disk allowance in the reviewed command |
| Publication | Separate offline verifier before same-parent staging/rename; exclusive sibling lock; existing paths rejected | Dedicated local volume and operator-controlled paths; no network/cloud-synced share; intended filesystem/ACL/free-space evidence still required |
| No mutation | Frozen SQL has no data/schema writes; application services are not initialized | No migrations, indexes, runtime startup, repair, replay or Gajra production connection |

Approved target property: DESKTOP-1PJK2OB\FCINVENTORY.
Database: FactoryConnect_GG003_IMP1C_INVENTORY_20261020.
Machine: 677b030b-1ede-4a67-82ae-6a494791357f.
Processor key: metric-aggregation:677b030b-1ede-4a67-82ae-6a494791357f; stream key: metric-inputs.
The generic six-set Q2B from #140 remains reference-only and must not be substituted.

The exact seven result sets are:
1. Q2B-target
2. Q2B-cut
3. Q2B-contribution-boundary
4. Q2B-fact-membership
5. Q2B-duration-operands
6. Q2B-coverage
7. Q2B-end-target

Target, cut, contribution-boundary, coverage and end-target must each contain exactly one row. Full fact-membership count must reconcile with both boundary and coverage counts. Duration-operands is grouped membership diagnostics, not summed operands.
Do not require an assumed membership count of 534: it is the watermark, not a row-count assertion. Preserve every admitted member across dates/keys. Anomaly categories overlap; do not sum them.

## B — Execution checklist to complete before Q2B.3C

| Field / verification | Current state / required entry |
| --- | --- |
| Authorization ID, approver, operator, permitted time window | UNSET; explicit one-attempt approval required |
| Final #141 main commit and repository/source hashes | UNSET pending merge; hash all reader/exporter/schema/SQL source files on the exact checkout |
| Reader binary and dependency identity | UNSET; record build output hashes, SDK/runtime and the actual locked/resolved dependency inventory from the final validated #141 checkout, including transitive dependencies. Inspected SqlReader.csproj declares Microsoft.Data.SqlClient 7.1.1 and direct System.Security.Cryptography.Pkcs 9.0.21; declarations alone do not certify restored artifacts. Retain the restore lock/assets and build dependency manifests with hashes; no rebuild drift |
| Workstation and Windows process identity | UNSET; actual operator token must match approved dedicated inventory identity; no elevated administrator shell |
| expected_original_login / database principal | UNSET; exact observed/attested identity, never inferred from a connection alias |
| Existing backup provenance / immutable copy identity | Retain previously approved provenance; bind its record ID/hash here |
| Administrator permissions / visibility attestation | Bind existing attestation, confirm still applicable; no new grants or role changes authorized |
| Local working, spool, evidence, log and envelope paths | UNSET; absolute dedicated paths on approved local volume with ACLs, ownership, no reparse/network destination and adequate free space |
| Intended Windows publication filesystem | UNSET; bind offline validation on the intended volume and recovery review |
| Full invocation and tool versions | UNSET; exact executable, arguments, working directory, exit-code capture, time/resource limits and source hash checks must be reviewed |
| Process supervision / cleanup allowance | UNSET; define bounded termination and review policy; do not claim cancellation guarantees from synthetic tests |
| Disk/output budget | Spool <=10 MiB; final data+manifest <=10 MiB independently. Allow retained spool, receipt, logs, evidence and temporary staging together; define aggregate local budget |
| Evidence handling and reviewer | UNSET; retain SQL, sources/build manifest, envelope, stdout/stderr, exit codes, spool, sidecar, data.jsonl, manifest.json and independent SHA-256/byte-count inventory |

No live command or completed authorization envelope is supplied here. A JSON authorized flag is only a technical gate; it is not approval.
The reader accepts --execute-authorized plus envelope, SQL and new spool paths. The separate capture_spool.py requires spool, SQL, new output directory, exact expected login and authorization ID. Freeze both commands together before approval; do not run either now.

Required envelope values for a later review: authorized must remain false until Q2B.3C approval; authorization_id and expected_original_login UNSET; server/database exactly as above; sql_sha256 lowercase dbb7af00aa97241f789d982b43a5740c774a79973360bcb6ba66d51d8cac7719; timeout_seconds 60; max_spool_bytes 10485760.
The current reader does not independently enforce approval expiry, one-attempt consumption, output path policy or the preconnection expected-login match. These are explicit operator/supervisor gates. SQL rejects prohibited roles; the offline verifier checks the returned original login before final publication. No claim of stronger enforcement is made.

## Fail-closed and recovery rules

- Hash, target, principal, visibility, schema, count, resource or completion failure: stop, record failure, do not publish or interpret partial evidence.
- A completed spool is only capture material. Run independent offline validation before final evidence publication; neither stage proves historical correctness.
- Reader failure attempts removal of its own spool/sidecar; cleanup can fail. Inventory residual paths. Never regard existence alone as success and never manually create a success receipt.
- Exporter failure normally removes its own staging directory; a crash may retain staging/lock files. Preserve and review ownership before any separately approved cleanup. No automatic stale-lock stealing, overwrite, promotion or retry.
- Keep successful spool/sidecar and final evidence immutable under the approved retention policy. A retry is a new explicit authorization and fresh paths; do not silently rerun the database query.
- Preserve errors without secrets. No credentials in command lines, logs or repository documentation.
- Unknown live provider metadata or genuine cancellation behavior remains a stop condition. If additional fixture validation is required, request it separately; no database test is authorized by this review.

## Q2B.3C go/no-go record

Current result: NO-GO / execution HELD because operational identity, paths, invocation, artifacts and approval fields remain unfilled.
A reviewer may approve one bounded restricted-reader extraction only after all B fields and remaining live-use risks are accepted. The approval must bind exact hashes, target, operator, limits, paths and permitted attempt, and explicitly exclude production.
Merging this documentation cannot change NO-GO to GO.

## Later Q2B.4 reconciliation and Q2B.5 decision

First reconcile all ledger members and diagnostics. Reject arithmetic interpretation when exact references, machine/stream bindings or duplicates are unresolved.
Then reconstruct duration.running and duration.planned-production separately for the exact production-day and shift identities using reviewed .NET-decimal-compatible parsing, overflow checks and contribution order. Never filter facts to force the expected ratio. Current aggregates cannot substitute for revision-534 evidence.
The approximately 2683.61 running / 2452.01 planned seconds and 109.45% ratio are prior RCA diagnostics, not newly extracted results.

Keep three outcomes distinct:
- Aggregation error: evidence demonstrates incorrect admitted contributions, scope or aggregation arithmetic.
- Reporting interpretation error: admitted contributions are valid but operand selection or evaluator semantics misapply them.
- Insufficient evidence: retained facts/authority cannot establish either explanation.

Running outside planned production is not itself proof of corrupt source facts. Reproduction of the legacy number does not establish approved schedule policy, complete activity authority or V2 numeric feasibility.
IMP-1 stays open; historical numeric feasibility remains NotEstablished. Runtime implementation, schema changes, replay, repair and production deployment remain held.
