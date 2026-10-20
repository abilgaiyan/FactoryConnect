# Q2B.2 — Compile-only validation, estimated-plan review and output gate

Status: proposed validation procedure; no SQL connection, compilation, plan generation or evidence extraction executed.
Applies to Q2B.1 SQL reviewed at commit 868cae4a6e16ee837382f2099f107bbce475a4fc.
SQL path: Q2B-revision-534-lineage-review.sql.
Git blob: e51135ed263618cbc6a80887f7a49661dbf156cd.
The SQL remains unchanged by this procedure. A documentation commit changes PR head, not these SQL bytes.

## Materialized identity

Canonical file: UTF-8 without BOM, LF, 7694 bytes.
SHA256: dbb7af00aa97241f789d982b43a5740c774a79973360bcb6ba66d51d8cac7719.
CRLF UTF-8 without BOM derivative SHA256: 9e01e4d9f5c513275748ab1cd236874702435a818e749cfd9676c41102dffe30.
These hashes were computed offline from the fetched Git content; neither is an execution result.
Prefer the canonical file. Record actual local Get-FileHash SHA256, byte count, encoding/line endings and Git blob before validation. A BOM or other modification requires a separately identified/reviewed derivative; do not silently normalize after hashing.

## Gate V0 — Validation authorization

Record approved disposable target DESKTOP-1PJK2OB\\FCINVENTORY / FactoryConnect_GG003_IMP1C_INVENTORY_20261020, unchanged READ_ONLY custody, backup identity, operator, exact SQL/hash, validation-only scope, command/client version and evidence directory.
Q0/Q1 are completed; do not repeat setup/restoration or permission discovery. Retain their restricted-principal/administrator attestation. A current target-state assertion is connection binding, not a repeated Q0 inventory.
No credential grants, database option changes, data reads from Q2B, application/provider bootstrap or replay are included in this gate.
Validation commands and connection details require separate review/authorization before use. No runnable command is issued by this document.

## Gate V1 — Restricted-reader compile-only validation

Use a dedicated direct SQL session under the approved restricted reader and an independently verified connection target. Run a reviewed wrapper that enables SET NOEXEC ON in a separate batch, submits the exact frozen SQL batch, captures all errors/messages and then closes/discards the session.
Do not disable NOEXEC and resubmit the packet in the same connection. Session closure is the cleanup; do not return a NOEXEC session to a connection pool.
NOEXEC parses/compiles without running statements. Therefore the packet's target/role guards do not execute during this phase; independently pin the connection before submitting.
No SELECT result sets should be returned. Unexpected data results fail the validation boundary.
Compile success is not execution authorization, does not establish row counts or guard success and may not resolve all deferred names/permissions/branches. Record those limits explicitly; SET PARSEONLY alone is not a substitute for semantic compilation.
The wrapper, session behavior and exact server/client compatibility must be reviewed before authorization. No proof of SQL Server syntax success is claimed here.

## Gate V2 — Administrator-side estimated plans

Use a separate administrator-operated session on the same identified disposable copy solely for estimated plans. Never run the evidence packet under that administrator identity.
Enable SET SHOWPLAN_XML ON as its own batch; submit the exact SQL batch/parameters for estimated plans; close/discard the session afterward. Do not use actual-plan execution, SET STATISTICS XML with execution, or an application test harness.
SHOWPLAN_XML generates estimates without executing the packet, including its guards. Independently verify target and NO data extraction before this phase.
Restricted-reader privileges remain unchanged; no SHOWPLAN or elevated grants are added to that reader merely for this review.
Record estimated plans for guards and every output statement, compile errors/warnings and any deferred/omitted statement. If the server cannot compile the entire guarded batch, derive individually labeled SELECT batches using identical parameters/predicates. Hash each derivative and map it to source statement; review any discrepancy before accepting estimates.

Review the actual restored index definitions and statistics, not just expected repository names:
- Processor+Position contribution range access: Q1 identified unique UQ_MetricAggregationContribution_Position and primary processor/fact key.
- Exact fact lookup: Q1 identified fact row primary key and stream/position/row uniqueness. The three-part join must preserve missing references.
- Raw fact ID lookup for diagnostics must remain separate from exact join.
- Anti-membership stored-fact diagnostic, grouped duration keys, duplicate-group subqueries and pre-extraction COUNT guard also need estimates.
- Evaluate estimated rows/rows read, seeks versus scans, join cardinality, sorts/memory, conversions and all predicate bindings. A 1000-row extraction guard or <=534 unique positions does not bound scan cost.
- No indexes, hints, statistics updates, schema repair or database configuration changes are part of validation.

Capture operator/reviewer conclusion, timeout and output/memory bounds. An estimated plan does not establish actual runtime duration, actual I/O or successful execution. Actual execution plan remains NotEstablished unless separately authorized execution later captures one.

## Gate V3 — Scope and anomaly acceptance

Extraction covers ALL exact processor ledger members through watermark 534, including other days/shifts. It is not prefiltered to the RCA production period.
Offline arithmetic first partitions by exact production-day site/date or full shift occurrence identity. Reconcile against the specific historical operand scope before interpreting the approximately 109.45% diagnostic.
Never select every stored fact <=534, filter only running, or match a day by shift label alone.
Coverage columns overlap. A referenced missing fact can increase ReferencedFactAbsent, ExactJoinMissing and FactMachineUnverifiable simultaneously; never sum these into a unique error count.
No clean machine conclusion follows from WrongExistingFactMachine=0 if FactMachineUnverifiable>0.
The seven result-set row counts are not known before extraction; retain the <=1000 full-membership abort bound and report actual counts later.

## Gate V4 — Lossless output contract before evidence extraction

Do not use sqlcmd -w 220 as the authoritative lineage export. Wider formatting alone does not ensure delimiters, NULL, Unicode or row boundaries survive.
Preferred separately reviewed exporter: direct read-only ADO.NET SqlDataReader, sequential seven result sets, typed UTF-8 JSON records:
- Record result-set ordinal/name, column names and SQL types, row ordinal and row counts.
- Strings (including MetricValue) are JSON strings with escaping, never trimmed or numerically recast.
- Binary identities are lossless uppercase hex; decimals/integers are invariant decimal strings, datetimeoffset round-trip strings, GUIDs canonical strings, NULL explicit JSON null.
- Validate exactly seven sets and final target/read-only row. Flush complete output to a local temporary artifact; only mark complete after command success, all sets captured and SHA256/byte counts recorded.
- No silent client field/row truncation or output-limit cutoff. On timeout/10 MiB limit/error, stop and label capture Incomplete; no arithmetic/completeness conclusion.
- Reader/exporter must not start mutating services, run migration checks or invoke processing APIs. Keep parameters/SQL frozen and record any exporter transformation.

Exporter implementation and synthetic round-trip tests remain pending separate review. Test whitespace, quotes, delimiters, CR/LF, Unicode, NULL versus empty, long identifiers, binary data and exact numeric text without database writes.
A human-readable sqlcmd export may be supplementary, not authoritative, unless an independently reviewed escaping/round-trip format proves losslessness.
No exporter or data-extraction command is delivered/authorized by this procedure.

## Exit record

| Evidence | Current state |
| --- | --- |
| Exact SQL blob and offline file SHA256 | Recorded above |
| Restricted-reader compile-only result | NotExecuted |
| Administrator estimated-plan/index review | NotExecuted |
| Actual runtime plan/performance | NotEstablished |
| RCA period identity and seven-set extraction counts | NotEstablished |
| Lossless exporter validation | Pending |
| Q2B evidence execution authorization | Held |
| Historical operand reproduction/numeric feasibility | NotEstablished |

Keep #141 draft until required validation/review gates are accepted. This procedure does not authorize compile/plan connections by itself.
No source-data modification, reporting replay, runtime implementation or Gajra production access is authorized.
