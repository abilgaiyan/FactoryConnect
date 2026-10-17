# C1–C8 validation-only prototype contract

This is an executable design probe, not B2. Existing 021 definitions, production source and codec registrations are unchanged. The accepted schema/Windows checkpoint remains `7cd36af`; it is not reopened by a prototype setup failure. PR #121 remains draft and unmerged.

## Actual paths and explicit substitutions

The fixture uses actual SQL input/aggregation stores, initial R0 convergence, standard publication, transition Begin/Complete, claim-bound admission and legacy outcome replay. The actual projection commit transaction supplies its Serializable transaction, processor gate, processor/checkpoint acquisition and final checkpoint write/commit/rollback. The prototype continues projection → manifest → evidence acquisition in that order with explicit SQL; it does not call the V1 row materializer for containment results. SQL foreign keys also acquire implicit source locks before explicit validation; those must be included in trace review.

The archive/retained publication body is test-only SQL against the accepted proposed tables. It clones key/context columns from a genuinely published V1 template. The reporting prototype uses the existing ReadCommitted `StableRead` transaction/shared gate and reads the test receipt and manifested retained binding inside that transaction. It deliberately does not claim that the production V1 detail materializer understands V2.

Receipts use explicit polymorphic shift/day tags so abstract period properties cannot lose scope identity. Receipts are deterministic fixture JSON with `ValidationOnly=true`, full source/key/policy/snapshot/witness and optional R/outcome binding. Their digest domains begin `B1ValidationOnly/`. They are **not FCWE V1/FCDS V2 bytes**, despite the structural proposed-schema codec columns. They are confined to temporary databases and distinct `b1-validation-only:*` processor identities. Production codec/publication/API implementation remains held. The already accepted independent canonical vectors remain separate evidence.

The authority validator reads only on the caller's connection/transaction. It applies explicit HOLDLOCK source queries, including for the ReadCommitted reporting path. It materializes facts with the existing historical materializer, rebuilds aggregates/witness with the existing Core code and invokes the existing opt-in evaluator over that SQL snapshot. It reads the target transition, exact `(A,R)` pair and outcomes, reconstructs quantity sources with the existing source reconstructor, reads standards under the same transaction and independently recomputes resolutions using the existing resolver. Reflection reuses private materializers/reconstructors in the test assembly; no reflection seam is added to production code.

The fixture is one synthetic machine/shift: initial A1, bound A4, one quantity source, running contribution at position 3 (3 seconds, explicitly unplanned), planned contribution at position 4 (4 seconds), approved standard S1 and terminal R1. Availability is below 100% and has a real positive numerator witness. Neighboring contributions/targets/R2 deliberately exercise cut exclusion. C3 uses two quantity sources (bound A5, witness position 4, terminal R2): it tests one committed source plus a second uncommitted admission while the transition remains pending. This is a controlled concurrency fixture, not the GG-003 factory-provenance fixture.

## Source acquisition candidate

After the existing publication prefix, result slot and archive/reference stages, explicit validation reads exact aggregation revision → ledger/facts/stream → target terminal transition → exact R and `(A,R)` pair → outcomes/conflicts through R → standards through S. Pending or absent terminal authority throws `HistoricalEvidenceUnavailable` and rolls back. Contradictions fail validation; SQL failures are not caught as missing authority. Existing archive FKs take source S locks earlier; actual traces, not this prose sequence alone, determine physical acquisition.

## Schedules

| Gate | Executable schedule and assertion |
| --- | --- |
| C1 | Pause publication after its prefix; actual aggregation commits a later running contribution. Resume and reconstruct A4/R1, with numerator 3 and witness position 3 unchanged. |
| C2 | Real Begin or Complete waits in a fixture trigger after its INSERT/UPDATE. Run both source-first and publication-prefix-first variants. Observe validator's SQL wait on the actual writer. Committed pending authority rejects publication; committed complete authority reconstructs the whole cut. |
| C3 | Real admission waits after outcome INSERT. Run both prefix-first and admission-first variants. Pending authority rejects publication before it can claim a missing metric; partial outcomes remain unpublished. Admission alone is still pending; actual Complete then enables exact reconstruction. |
| C4 | Same processor contenders queue at the real gate. Identical replay validates history and produces zero additional mutation-audit rows; changed bound content conflicts without mutation. |
| C5 | Existing shared reporting gate versus publication in both orders: initial coherent empty state or complete manifested receipt; never an intermediate archive/projection/evidence set. |
| C6 | Different projection processors contend for the same actual witness identity in both publisher orders. Observe the real blocking resource (which may be a result-slot gap), then assert exactly one immutable witness and complete separate publications. A separate ancillary archive-layer probe supplies actual shift/day witnesses in opposite input enumerations to the same sorted writer and observes serialization before asserting two immutable identities. This archive-layer probe is distinct from full result publication. |
| C7 | Actual aggregation/R0/cut competes with a validator holding A4/R1. Actual legacy replay and neighbor Begin run, then real neighbor admission competes with old-cut reconstruction; neighbor Complete reaches R2 while old A4/R1 remains unchanged. This is a bounded observed graph, not a universal deadlock-freedom proof. |
| C8 | Inject failures after prefix, slot, witness, result, reference, validation, projection, evidence, manifest and before checkpoint; a fixture checkpoint trigger fails after the real checkpoint update; cancel before checkpoint. Assert archives/evidence/manifest absent, checkpoint A1, then successful restart at A4. |

## Retained observations and assessment rules

TRX output contains `B1_CONCURRENCY_EVENTS`: reviewed head/build SHA, synthetic database, engine version, transaction/session IDs, stages, SQL requests/waits and held/requested locks with object/resource identifiers. State observations include witness/result/reference/projection/evidence/manifest counts, checkpoint and mutation audit. Each case owns an isolated database and a unique server Extended Events session. SQL and stored-procedure statement events retain source-writer session/transaction IDs, including fast replay; matching deadlock XML graphs are retained before session/database cleanup. An observed end-marker and zero session drops/target truncation are required. No retry masks deadlocks. An empty graph list only means no matching graph was observed; a SQL 1205 remains a failure even if event delivery has not produced a graph.

SQL app locks in fixture triggers provide deterministic writer barriers. Polling must observe the matching `blocking_session_id` or explicitly record completion where completion is allowed. Elapsed time alone is never success. The observer uses a separate connection only for telemetry; source validation does not.

The first executable attempt at `3d8ad748` (run `39058999647`) built but all 20 original cases failed during setup because the test aggregate lookup omitted the existing operand-to-durable-key mapping. No schedule acceptance is inferred from that run. Later revisions retain it separately and use `OperationalMetricComponentKeyMapping.AggregateKey`. Run `39059453630` at `ec571ece` likewise stopped in setup: the synthetic duration fixture used a display unit instead of `MetricInputFactUnits.Seconds` (`s`). That attempt proves no concurrency gate. The fixture now uses native unit constants, and CI checks the C1 fixture path before launching the full matrix.

## Reproduction

Use a local development SQL instance or the CI SQL service with permission to create/drop fixture databases and create/read/drop the fixture Extended Events session. Never use a factory or RCA database. Set `FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING` to the isolated instance's master catalog and `B1_VALIDATION_HEAD` to the checked-out commit. Run:

```powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~B1ConcurrencyAcceptanceTests" --logger "trx;LogFileName=b1-concurrency.trx" --results-directory TestResults/B1Concurrency
```

CI uploads `b1-concurrency-evidence`. Test execution PASS is not automatic B1 acceptance: review the traces and the explicit limitations, especially the distinction between the C6 archive-layer ordering probe and full publication, and the C7 observed graph, before any freeze decision.

Run `39059934601` at `d4bdf60f` reached the real evaluator but its smoke test rejected the prototype adapter's noncanonical operand aliases. No matrix acceptance is inferred from that attempt; the adapter now maps the canonical request aliases onto the same SQL-derived component values.
