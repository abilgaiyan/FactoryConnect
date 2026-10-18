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

P1 at `8208ad6b` passed both CI jobs. P2 now registers migration 021 and its
twelve-command execution plan with physical schema authority and upgrade/rollback
tests; see [P2 schema implementation](GG003-stage-b-p2-schema.md). P2 SQL acceptance passed at `5f830ed7`: both `build-and-test` and
`sql-server-integration` completed successfully after the legacy snapshot
comparison was updated to account for migration-021 defaulted columns.
P3 contribution and Availability authority are now implemented as described below;
Performance R/S and recursive validation, P4 and P5 remain pending. The publication
guard remains intact.

Migration 021 is registered with twelve command boundaries and caller-owned
transaction semantics. The schema prototype established why one combined DDL
command is not sufficient. Preserve the registered migration's checked-in
checksum, execution boundaries and physical-schema validation during P3–P5.

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

## P3 start gate — 18 October 2026

P2 acceptance permits implementation of the historical authority validator,
**not** activation of containment publication. P3 must:

1. Accept the caller's open SQL connection and active transaction. Never open a
   second connection or start/commit an independent transaction for authority reads.
2. Bind the exact aggregation processor, input stream and revision A; reconstruct
   only incorporated contributions at or before A. Reject absent, duplicate,
   conflicting and out-of-cut membership rather than relying on an envelope hash.
3. Recompute both complete Availability aggregates and the deterministic first
   offending RunningDuration contribution; verify its complete fact, scope and
   original position, not just its witness ID.
4. For Performance, require the completed transition and exact terminal R,
   complete `(A,R)` source cut, produced-source set and selected outcome/standard
   revisions S. Pending reference-time transitions are not verified missing data.
5. Validate every FCDS V2 dependency recursively, including OEE children whose
   Availability result is Unavailable. Distinguish missing authority from
   contradictory/corrupt authority and unsupported codec versions.
6. Cover stable historical cuts, later-source exclusions, changed contributions,
   incomplete transitions, missing outcomes/standards, forged but canonical
   witness/result envelopes and concurrency with source writers. Verify zero
   writes by P3 itself.

Acceptance evidence must exercise the production SQL reader/validator seam with
real SQL transactions; a mock-only test or the 34-contribution historical fixture
is insufficient. Keep the Stage-A publication guard and separate processor
registration unchanged until P4 and composition are explicitly accepted.

## P3 implementation ledger

- `ff9a8f3d`: added `SqlContainmentHistoricalContributionReader`, a production
  SQL reader taking the caller's `SqlConnection` and `SqlTransaction`.
  It checks the exact processor/stream/revision binding and enumerates admitted
  contribution facts at or before that cut in stable position order, retaining
  original fact and period-ownership fields. No independent connection,
  transaction or write is opened by this reader.
- At `ff9a8f3d`, this was only a source-membership primitive. The following
  Availability slice adds aggregate/witness reconstruction and SQL tests. Completed
  R transitions, outcomes/standards S and recursive result verification remain
  pending. Do not wire this seam to publication or reporting reads before P3–P5
  acceptance.

### P3 contribution and Availability implementation

The reader now returns complete `PositionedMetricInputFact` instances using the
existing historical fact materializer, including every domain fact field and
both period identities. It resolves the logical processor and stream from the
claimed cut, requires its exact revision, and reads only incorporated membership
through A with the composite fact/stream/position join. Broken joins and duplicate
membership are rejected. It requires the caller's open Serializable transaction;
no equivalent isolation modes are currently admitted.

`MissingContainmentHistoricalAuthorityException` distinguishes absent processor
or revision authority from `ContainmentHistoricalIntegrityException` for binding,
materialization, membership, aggregate or witness contradictions. SQL errors and
cancellation are not translated into authority errors.

`SqlContainmentAvailabilityAuthorityValidator` independently rebuilds both fixed
Availability operands from these SQL contributions. The existing deterministic
Core builder validates duration/eligibility semantics and selects the lowest
position. Verification compares complete canonical FCWE bytes against the
reconstructed witness, including all fact fields, period, cut, aggregate counts
and timestamp bounds. This seam verifies Availability witnesses only; it does
not validate an FCDS result or certify a missing Performance operand.

Eleven new real SQL cases cover full fact equality, raw unincorporated facts
inside A, later incorporated facts, shift/day aggregates, canonical forgeries,
missing/corrupt authority, malformed values, cancellation, isolation/transaction
ownership, caller-visible updates and rollback, source-writer lock contention,
and zero mutations across source/projection/archive snapshots. The named CI
step retains a TRX artifact before the full SQL suite. SQL results remain pending
on the implementation head. Performance completed R/S reconstruction and recursive
FCDS V2 verification remain required for P3 acceptance. No publication/read wiring,
guard change, migration change or factory activation is included.

Local validation for this Availability slice: all 759 ordinary Release Integration
tests passed; the integration project compiled successfully. No local SQL pass
is claimed; the new SQL cases require the dedicated CI gate.

The initial P3 CI attempt at `152730e9` (run `39150990424`) passed ten of
11 focused SQL cases. The remaining case failed before reconstruction because
the shared legacy fixture did not create the migration-021 archive tables needed
for mutation snapshots. The P3 fixture now creates a disposable current-schema
database through the actual migration engine; the snapshot assertions remain
intact. This correction does not change the production reader or validator.
