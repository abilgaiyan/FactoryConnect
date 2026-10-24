# GG-003 PR #152 real-data performance harness

Status: Stage A duration regressions passed locally (3/3); Stage B prepared,
not compiled or executed by the assistant. Do not run the fresh clone until
the new source builds, synthetic tests pass and this harness is reviewed.

Branch: `test/gg003-pr152-real-data-performance`.

## Safe validation before any database access

From the developer checkout, pull this branch, then run:

```powershell
$results = 'artifacts\TestResults\gg003-pr152-validation\stage-b-preflight'
dotnet test tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj `
  -c Release `
  --filter '(FullyQualifiedName~ContainmentHistoricalSqlCounterTests|FullyQualifiedName~Gg003PerformanceGuardTests)&Category!=SqlServerIntegration' `
  --results-directory $results `
  --logger 'trx;LogFileName=stage-b-preflight.trx'
if ($LASTEXITCODE -ne 0) { throw 'Preflight failed; do not run the performance clone.' }
git diff --check
git status --short
```

Expected: 8 synthetic cases (four counter cases and four target-guard cases).
No SQL connection is opened by these cases. Overlapping command events carry
distinct processor/cut identities and success/error outcomes; both must retain
durations. Synthetic connections have null transaction identities; existing
SQL execution-count tests cover non-null transaction separation.

## Guard and write boundary

The performance fact is opt-in through all of:

- Windows and `FACTORYCONNECT_GG003_PERF_CONNECTION_STRING`.
- Exact server `(localdb)\MSSQLLocalDB`, integrated authentication, no attach-file override.
- Exact case-sensitive catalog `FactoryConnect_GG003_STAGEB_REPLAY_PR152_PERF_20261024`.
- `FACTORYCONNECT_GG003_PERF_CONFIRM=BOOTSTRAP534_ADVANCE537`.
- `FACTORYCONNECT_GG003_PERF_REPO_ROOT` identifying the developer checkout.

The fact verifies SQL's actual database name and requires an absent containment
checkpoint, legacy checkpoint 524 and exact source cuts 534 and 537 before writes.
It does not apply migrations or restore a backup. Required schema must already
exist. It does not launch Edge, seed source facts or run the legacy publisher.

When separately released for local execution, the fact will write containment
publication/archive state and actual reference-time convergence state in this
one disposable database. It bootstraps via the actual runtime, capped at 534,
then runs one actual cycle capped at 537. This is a destructive experiment
with respect to its starting state. Failure can leave committed bootstrap or
advancement work: reconcile the checkpoint manually and use a fresh restore
for another advancement measurement. Never retry automatically or redirect
to a preserved revision-537 clone. The assistant has not opened this database.

## Measurements and acceptance

Reports use a unique JSONL file below the supplied checkout's
`artifacts/TestResults/gg003-pr152-validation/stage-b`, retaining earlier
phase reports on failure. No connection strings, SQL text, payloads or exception
messages are written; errors are recorded by type only.

Bootstrap duration includes per-cycle verification reads. Advancement cycle
duration includes runtime checkpoint restoration, convergence, evaluation and
publication. Exact publisher replay has its own duration.
Per-command transaction ordinals are local to each phase; identity fields and
SqlClient Before-to-After/Error durations are recorded separately for runtime,
reporting and replay. The listener measures ExecuteReader diagnostic duration,
not complete historical load/materialization duration.

One reporting checkpoint request is started while a historical command is pending,
if observed within 30 seconds. Record request latency, returned revision or error
type, whether the cycle was active and whether historical SQL was pending. The
probe is not deliberately held at a gate and adds no DMV sampler or writer barrier.
A missed overlap or failed report is coverage/performance evidence, not proof of
successful reporting. A passing workload test can contain a failed reporting
probe; inspect the JSONL before accepting reporting performance.

No Exclusive acquisition/release boundaries or Shared-gate wait boundaries are
observed. Never describe cycle/publication/request timing as gate ownership/wait.
Peak memory is not measured by this harness and remains pending.
There is no baseline comparison or automatic latency threshold; performance
acceptance remains a separate decision even when workload correctness passes.

Integrity checks cover bootstrap's accepted revision-380 fact witness and operands,
nested OEE binding, all pre-existing witness archive rows retained unchanged,
legacy checkpoint 524, containment checkpoint 537, source/legacy/revision-ledger
fingerprints, and unchanged complete publication snapshot after actual exact
publisher replay at 537. The advancement runtime's historical loads must be unique
per transaction/processor/stream/cut. The experiment stops at 537; it does not
claim to catch up to source head 741 or recover Gajra production reports.


## Transaction-start instrumentation correction

Stage B is held following a reported bootstrap assertion failure. Production historical
reader transaction binding is unchanged. Disposing a transaction does not turn a
captured reference into null; the earlier failure's precise cause is not established.

Completed diagnostic records now contain an ordinal and immutable start-time flags,
rather than a live SqlTransaction reference. Ordinals are local to each counter and
assigned by reference identity through a ConditionalWeakTable. The same instance keeps
one ordinal; distinct instances never intentionally share one. No SQL transaction is
kept alive by completed records. Missing transactions use ordinal zero as an invalid
sentinel, never an invented shared identity; SQL regression/harness validation rejects
missing transactions and mismatched connection bindings before grouping exact cuts.

Synthetic regressions exercise absent/mismatched bindings, immutable snapshots, and
concurrent stable/distinct identity assignment using opaque object tokens. Existing
SQL regression tests assert the real command-start flags after publisher/read transactions
finish. Synthetic tests do not prove actual SqlClient transaction binding.

Safe preflight now expects 12 cases: eight counter cases and four target-guard cases.
Do not rerun bootstrap on the already-mutated clone. Inspect its actual database identity,
migration history, aggregation checkpoint and all projection processor checkpoints first.
A state check is not a resume command; the harness still requires no containment checkpoint.
Preserve prior JSONL evidence. Reported long SQL event durations remain observations,
with cause and gate timing unestablished.


## Advance-only 534 to 537 experiment

The inspected transaction-start TRX passed 12/12. Developer SQL readback showed schema
21, legacy 524, containment 534 and aggregation 741. Preserve the successful bootstrap
and all earlier reports. The fresh-start test is unsuitable for this mutated clone.

`Gg003RealDataPerformanceTests.AdvanceOnly534To537WithMeasuredHistoricalSqlAndConcurrentReporting`
is a separate opt-in fact. It uses only the already guarded exact database and requires
migration IDs 1..21, exact containment 534, legacy 524, aggregation head 741, exact source
537 and next revision 537. It never calls bootstrap or migration code. It will consume
the clone's revision-534 state when run. If a reusable 534 state is needed, take and
verify a separate backup/clone first; this harness does neither automatically.

Before running SQL, compile and execute the existing synthetic cases plus the four
new interval-overlap cases:

```powershell
dotnet test tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj `
  -c Release `
  --filter '(FullyQualifiedName~ContainmentHistoricalSqlCounterTests|FullyQualifiedName~Gg003PerformanceGuardTests|FullyQualifiedName~Gg003AdvanceOnlyOverlapTests)&Category!=SqlServerIntegration' `
  --results-directory 'artifacts\TestResults\gg003-pr152-validation\advance-only-preflight' `
  --logger 'trx;LogFileName=advance-only-preflight.trx'
```

Expected: 16/16. This addition has not been compiled or executed by the assistant.
Use the exact test filter, never the entire performance class, when later releasing SQL:

- Connection variable: `FACTORYCONNECT_GG003_PERF_CONNECTION_STRING` (same exact LocalDB clone).
- Repository root variable: `FACTORYCONNECT_GG003_PERF_REPO_ROOT`.
- Separate consent variable: `FACTORYCONNECT_GG003_PERF_ADVANCE_CONFIRM=ADVANCE534_TO537`.
- Clear that consent variable after execution, including failures.
- Filter: `FullyQualifiedName=FactoryConnect.Integration.Tests.Gg003RealDataPerformanceTests.AdvanceOnly534To537WithMeasuredHistoricalSqlAndConcurrentReporting`.
- Keep TRX beneath `artifacts/TestResults/gg003-pr152-validation/advance-only`.

A new uniquely named `AdvanceOnly534To537-<GUID>.jsonl` is created in that directory.
Earlier JSONL/TRX files are not overwritten by the harness. Baselines include hashes
of source/legacy/revision-ledger state, the complete publication snapshot (containment
and legacy rows), and the retained witness archive rows. Raw evidence is not emitted.

The report distinguishes:

1. Correctness: verified checkpoint 537, exact-537 latest-manifest projections, coherent
   witness/OEE binding, preserved older witness archive rows, source/legacy isolation,
   aggregation still 741, and unchanged complete publication state after exact publisher replay.
2. Instrumentation: nonempty runtime/replay events, valid start bindings, completed
   successful commands with durations, no listener error/pending commands, and no repeated
   transaction/exact-cut load. A successful reporting read must also have observed historical
   events. Observed reporting historical-command errors fail instrumentation but remain recorded.
3. Concurrency coverage: PASS only when measured monotonic runtime invocation and reporting
   request intervals intersect. Otherwise MISSED (or NOT_ATTEMPTED); a passing correctness
   test does not upgrade this result. Reporting success/revision/error is recorded independently.

The operation interval starts before RunCycleAsync and ends in its finally block,
independently of how long the test waits to observe it. The reporting interval is
measured independently. This establishes invocation overlap only, not SQL-request or
gate overlap. No Exclusive ownership, Shared-gate wait or peak-memory measurement is
claimed. There is no automatic performance-acceptance threshold.

Final evidence is emitted on success or failure. On failure, read-only durable checkpoint
reconciliation is attempted with a separate bounded token. No automatic resume/retry is
permitted, even if failure occurred after commit. A failed instrumentation check can
coexist with Correctness=PASS; preserve both outcomes. Failed integrity verification leaves
Correctness=NOT_COMPLETED rather than claiming rollback. Neither outcome claims production recovery.
