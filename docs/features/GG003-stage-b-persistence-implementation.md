# GG-003 Stage-B persistence implementation contract

Baseline: `31aec8a71701b4c11aa49378582c321b54cc709b`, 18 October 2026.
Status: implementation started; durable containment publication is still unavailable.

## Authority and terminology

This implementation map records the authorization to start persistence. The
accepted RC2 specification remains a separately retained review reference; its
complete text and package are not redistributed in this branch. In particular,
the historical authority binding is mandatory in V2.

Roadmap B1 means production persistence, B2 means composition, B3 means restored
replay. The original RC2 document calls its design review B1 and its implementation
B2. References to those historical labels must not change the execution order.

PR #121 is merged in baseline history as `50138db`. Its schema/concurrency probes
remain design-validation evidence: their B1ValidationOnly JSON receipts are not
FCWE/FCDS and do not establish production publisher, decoder or read acceptance.
The accepted schema and concurrency checkpoints are retained. Production tests
must exercise the new materializers and validator rather than rerun the prototype
and claim it proves those implementations.

## Fixed contract

| Area | Required implementation |
| --- | --- |
| Policy and routing | `availability-scope-containment/1`; exact processor from `AvailabilityContainmentV1.ProjectionProcessorId`; independent checkpoint; all outcomes under this policy use format 2 |
| Legacy | Preserve historical SQL/checksums, FCEK V1, FCDS V1 bytes, reason vocabulary and default composition; never reinterpret old results |
| Witness | FCWE V1, complete fact and both aggregates, exact historical cut, normalized operand names, deterministic lowest offending position |
| Result | Recursive FCDS V2 with mandatory policy and per-node historical authority binding; fixed built-in 1.0 unpartitioned shift/day definitions |
| Identity | Three distinct zero-terminated digest domains from RC2 section 5; exact full bytes compared after hash matches |
| Slot | Projection processor + exact FCEK bytes + source revision; changed content conflicts even if ResultId changes |
| SQL | RC2 archives, complete CHECK/FK/trigger definitions; no CASCADE; no archive backfill from legacy rows |
| Atomicity | Witness/result/reference/retained projection/evidence/manifest/checkpoint share one existing publication transaction |
| Replay | Identical current replay verifies history and all bound content with zero writes; conflicts roll back; older checkpoint replay remains rejected |
| Read | Existing stable read discipline; exact processor and bound receipt; verify canonical bytes, semantic binding and historical authority before returning any page |
| Errors | Missing authority, stored integrity failure and unsupported version remain distinct; no fallback processor/revision or ordinary metric reason substitution |

## Production implementation sequence

| Slice | Concrete source work | Acceptance gate |
| --- | --- | --- |
| P1 — canonical formats | FCWE V1 and FCDS V2 encoders/strict bounded decoders, historical-binding model, identity and reference-path codecs | Seven accepted RC2 envelope vectors and all three domains byte-for-byte; six existing negatives plus allocation/depth/Unicode/order/semantic forgeries; unchanged V1 golden bytes |
| P2 — schema authority | Forward migration after current 020, explicit 12-command execution on one connection/transaction, catalog and startup/physical-schema descriptors | Fresh creation, 020 upgrade, rollback, rerun, actual constraints/triggers, preserved 001–020 checksums and populated V1 replay |
| P3 — historical validator | Transaction-aware contribution, completed transition/cut, outcome and standard readers; deterministic independent snapshot/evaluation reconstruction | Uses caller's connection/transaction throughout; exact A/R/S and source membership; missing authority differs from contradiction; later contributions excluded |
| P4 — publication | Format-aware processor registration and commit validation, preflight, archive slots/identities/references and retained publisher; complete replay comparison | Actual production paths satisfy atomic rollback, identical replay, changed-slot rejection, shared witnesses and source-writer/read interleavings |
| P5 — reads and API | V1/V2 dispatch, verified query/detail/summary materialization, per-metric metadata and controlled evidence errors; regenerate OpenAPI/client contract | Exact-processor isolation; whole-page failure; null metadata for synthesized missing cells; all three routes and Daily/Range failure composition |

P1 now includes FCWE V1 and FCDS V2 encoders/decoders, historical-binding claims,
bounded digest and NodePath primitives, and executable comparisons with all seven
accepted envelope vectors. The private review document, reference calculators
and complete package are not redistributed. The test resource contains the
required sanitized vector inputs/bytes/digests and negatives only, plus a V1
regression specimen. See [P1 implementation and validation](GG003-stage-b-p1-codecs.md).
P1 review/CI acceptance does not complete persistence or enable publication.

P2's migration ID must be checked again when registering it. The schema prototype
demonstrated that sending all DDL as one command fails during compilation;
production execution must preserve the twelve batch boundaries without GO,
connection changes, independent commits or weakened constraints. Existing
`SqlServerMigrationExecutor` currently submits a single command, so registration
requires an explicit reviewed execution extension and its transaction tests.

## Publication and historical validation

Preflight encodes and checks the complete batch without mutation. Inside the
existing Serializable publication transaction preserve processor → checkpoint →
projection → manifest → evidence acquisition. Acquire result-slot ranges, then
witness keys, result IDs and reference prefixes in the RC2 deterministic order.
Validate historical authority using that same transaction before any mutation.
Account for implicit source locks acquired by FKs, as the accepted traces do.

Aggregation cut A includes incorporated contributions only. Availability,
Utilization and Quality use their specified direct operands. Performance also
requires the exact completed transition for A, its terminal R, the `(A,R)` cut,
the complete produced-source set and the selected outcome/standard revisions S.
The current public historical reader opens additional connections and cannot be
used unchanged as this validator. A pending transition is not verified missing
reference time. OEE validates all children even when Availability is unavailable.
The separate assumed-quality and derived provisional OEE stores cannot be
substituted into these fixed built-in definitions.

Compare full canonical/index content and the complete reference set for every
existing slot/digest before writes. Insert immutable archives only for a new
publication, update the retained set through its normal coordinator, then advance
the checkpoint and commit once. Replaying cannot repair missing archive rows.
Independent reads perform the same authority checks; a hash is never membership
proof. No source history mutation is needed for this work.

## API boundary

RC2 section 17 governs the existing three reporting query routes. Per metric,
add explicit `publicationFormatVersion`, `evaluationPolicyId`, `resultId` and
`conflictWitnessId`. A nested Availability witness does not become an OEE root
witness. Unknown codec → 501; recognized malformed evidence → 500; canonical
evidence lacking required historical authority → 503. Keep existing 400/409
contracts distinct and do not catch arbitrary SQL/runtime errors as authority
failures. Verification failure rejects the whole page and new report composition.

## Completion and subsequent work

Persistence is complete only after P1–P5 pass on the reviewed head through actual
production paths, including the SQL suite. Until then retain Stage A's publication
guard and keep containment composition disabled. The original RC2 guard can be
replaced only with complete format/schema/processor validation, never simply
removed to permit tests or replay.

Composition then selects the separate containment processor explicitly. The
subsequent disposable GG-003 restore acceptance must publish actual revision 534
with Availability `Unavailable / RunningOutsidePlannedProduction`, null value,
the exact position-380 witness and original operands, plus nested OEE evidence.
Verify atomic checkpoint advancement, restart, idempotence, conflict rejection,
immutable historical receipts after later advancement and exact API retrieval.
The 34-contribution fixture is not a complete factory-ledger replay.

This branch does not perform a restore, move a checkpoint, alter factory
configuration or activate a release. Equipment Performance can continue as its
own customer-facing slice; full report acceptance remains separate from this
persistence work.

## Validation of the foundation slice

- Added ordinary .NET tests for three independent digest probes, envelope bounds,
  exact NodePath bytes, malformed lengths/counts and maximum depth.
- No local .NET SDK is installed; no local .NET/SQL pass is claimed.
- CI results must be recorded against the actual PR head before merge.

These original foundation results are historical. Subsequent P1 implementation
and local SDK/test evidence are recorded in the linked P1 document.
