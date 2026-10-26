# D1-A1 diagnostic harness: execution and coverage

Investigation status: the uploaded original dba86f5 TRX was independently parsed:
21 passed / 2 failed. Boundary mutation failed with SQL 1205; the SQL-error test
expected 1222 but received client timeout -2. The revised setup below is uncompiled
and unexecuted in this environment. D1-A1 is not accepted. PR #159 remains unchanged.

## Scope and target

Use a newly restored disposable LocalDB database named exactly
`GG003_ACQ_D1A1_20261026`, restored from the original GG-003 baseline. A separate
acknowledgement must match that name. The guard verifies the restored GG-003 head
at 40,545, instance 1792061301, next sequence 482204. It rejects other names,
non-LocalDB instances and attached files. Never use or rename the advanced A2 clone
for these tests. Keep services disconnected.

All mutations target newly generated synthetic machines and `d1a1:` streams. No
restored GG-003 row is changed. Synthetic rows remain for inspection; restore before
another acceptance run. No migrations, constraints, hints, forced indexes or
production files are changed. Serializable is explicitly selected only in the
candidate's test-owned transactions. Tests share the existing nonparallel clone
collection inherited from PR #159.

## Compile and discover first (no SQL execution)

```powershell
Remove-Item Env:FACTORYCONNECT_GG003_D1A1_CONNECTION_STRING -ErrorAction SilentlyContinue
Remove-Item Env:FACTORYCONNECT_GG003_D1A1_DISPOSABLE_DATABASE -ErrorAction SilentlyContinue
dotnet restore tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj
dotnet build tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-restore
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --list-tests --filter 'FullyQualifiedName~Gg003ProtectedReadFeasibilityTests'
```

Compile must succeed before enabling SQL execution. This implementation environment
has no .NET SDK or SQL Server; these commands could not be executed here.

## Run the 23 complete-scope cases on the separate clone

```powershell
$env:FACTORYCONNECT_GG003_D1A1_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=GG003_ACQ_D1A1_20261026;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:FACTORYCONNECT_GG003_D1A1_DISPOSABLE_DATABASE = 'GG003_ACQ_D1A1_20261026'
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~Gg003ProtectedReadFeasibilityTests' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=gg003-D1A1.trx' --results-directory artifacts/TestResults/GG003-D1A1
Remove-Item Env:FACTORYCONNECT_GG003_D1A1_CONNECTION_STRING
Remove-Item Env:FACTORYCONNECT_GG003_D1A1_DISPOSABLE_DATABASE
```

There are now 24 discovered cases: 23 complete-scope cases plus an independently
opted-in original partial-window reproducer. The reproducer is skipped unless its
second opt-in is set; that skip is not a passing result for the original failure.
The variables differ from D1-C and the preserved A2 benchmark. Missing connection
means skip; a supplied but invalid configuration fails before seeding. Permissions
must allow actual plans (`SHOWPLAN`) and the lock/request/session DMVs.

## Executed query shapes, bounds and plans

The candidate source query has exact machine/binary-stream predicates and an OR of
exact `(InstanceId, Sequence)` pairs. It returns raw identity, position and all
content-equivalence columns. Boundary queries select exact positions with IN.
Keys are limited to 128 per query and result sets to requested count plus one
sentinel row. Excess/duplicate results fail; no successful result truncates rows.
The maximum-key case exercises both 128-key shapes, plus equal sequences in two
instances with different content. No index is forced.

`SET STATISTICS XML ON` wraps each actual lookup. The harness consumes the data and
actual-plan result sets, emits `ACTUAL_PLAN_XML` and accessed index names, and resets
statistics in finally. These are the diagnostic candidate predicates; they are not
borrowed favorable SELECTs and are not yet a production reuse algorithm.

The holder logs effective isolation, RCSI/snapshot settings, SQL version and session/
transaction IDs. Lock snapshots before verification now include both holder and
contender, with database-local HoBT-to-index mappings read separately.
The same holder connection polls for actual blocking, then releases its transaction;
no arbitrary sleeps coordinate actors. Missing required waits fail. SQL command
bounds and wait deadlines stop unsuccessful schedules. Deadlocks/timeouts remain
failures; no exception is accepted in place of required evidence.

## Coverage matrix

| Cases | What runs | Required evidence |
| --- | --- | --- |
| Complete-read-set predicates (6) | Content update, source delete, instance/sequence/position update, deletion of unreferenced historical boundary | Protect all four source identities needed by this four-publication fixture before its boundary probes and writer start; actual verifier executes twice; mutation then completes and fresh exact read is Corrupt |
| Absent predicates (2) | Insert absent source identity or absent boundary | Insert waits until rollback; not claimed to be a valid publication |
| Existing conflicts (2) | Conflicting identity and position inserts | Existing constraint fails with 2601/2627; may reject immediately or after observed wait |
| Precommitted verifier equivalence (5) | Valid, changed value, missing row, damaged envelope hash, unsupported payload version | Actual existing verifier in default and protected scopes returns matching Found/Corrupt/UnsupportedEncoding |
| Cancellation and SQL failure (1) | Cancelled actual verifier; writer holds source while both scopes use session-local LOCK_TIMEOUT 0 | OperationCanceledException and SQL 1222 propagate; session timeout restored, writer rolled back |
| Archive atomicity (3) | Verification failure, cancellation after tentative publication, explicit rollback | Actual archive helper plus test-owned staging rolls back byte-for-byte stream snapshots of all five structures |
| Synthetic append/replay measurement (1) | Equivalent fixture, default versus protected transaction | Actual archive and replay helpers execute; commands and transaction/replay duration reported; replay state unchanged |
| Reader contention (2) | Actual durable-observation reader waits on tentatively appended row, default/protected scope | Observed wait, eventual matching row after commit, reader latency including coordination reported |
| Maximum shapes (1) | 128 identity keys, 128 positions, cross-instance collisions | Actual plans, parameter/row counts, distinct identities and content preserved |

## Actual-verifier boundary and remaining gaps

Reflection invokes the existing private `ReadEvidenceAsync`, `AppendEvidenceAsync`
and `ValidateEvidenceReplayAsync` methods from the built production assembly. No
production test hook is added. Failing method resolution fails the test. This uses
the actual verifier; it does not reimplement its comparisons. The helpers have no
independent transaction ownership; tests pass their owning connection/transaction.

The candidate changes the *test-owned transaction protection*, not the verifier.
The repeated-verification case retains locks across two real complete verifier calls.
It does not pause one actual verifier between publication lookups or implement a
128-publication retrieval-window engine. Actual CommitAsync cannot be given this
candidate transaction without production changes, so archive tests use explicit
synthetic raw/checkpoint/contact staging followed by the actual archive helper.
Their results are helper-composition evidence, not full CommitAsync equivalence.

Post-stage cancellation invokes the actual verifier with a cancelled token and then
rolls back tentative state. This is not proof of every production commit/cancellation
race. Unsupported-version equivalence is included; invalid enum and unexpected-row
classification retain the existing regression requirements from PR #159. No CHECK
constraint is disabled to manufacture additional states here.

The synthetic timing test still performs the existing full-prefix verifier; it is
not an optimized bounded-reuse implementation and cannot demonstrate SQL savings.
Candidate timings include extra predicate probes and actual-plan instrumentation.
Source command counts exclude diagnostic probes, statistics reset commands and
staging DML; probe execution/parameter counts are separately logged. Repeated
verification likewise intentionally performs extra work. Do not sum nested spans.

Lock timing is a bracket from completed protected lookups to rollback, not exact
acquisition/release timing for every key. DMV wait milliseconds are samples. Writer
and reader elapsed times include coordination. The reader is the actual observation
reader, not an HTTP reporting request. On a deadlock, the original SQL 1205 is rethrown and a fresh diagnostic connection
attempts to read matching system_health ring-buffer graphs, filtered by start time,
both actor session IDs and clone database. No event session is created or changed.
Missing/asynchronous/truncated/unauthorized graph data does not disprove a deadlock.
The original partial-window reproducer remains unresolved and fails on SQL 1205.

No synthetic result replaces the preserved 81.31-second append reference. Real-history
performance, HTTP reporting contention, all mid-verifier mutation schedules, oversized
window state and optimized-path acceptance equivalence remain unverified. Preserve
TRX/plan output and review all coverage gaps before any D1-A2 proposal. No merge or
production deployment is authorized by this harness.


## Original failures and revised setup

The original boundary case fetched only (1,1) and (2,1), but the actual verifier
also needs (1,2) and (1,3). Captured plans accessed PK_MachineObservation for source
content and UQ_MachineObservation_StreamPosition for boundaries. Session 69 held
range protection through the position index; session 74 waited LCK_M_X on that
index before session 69 became deadlock victim during subsequent source retrieval.
The TRX has no deadlock graph or contender granted-lock snapshot. A clustered-key /
nonclustered-position-index order inversion is a hypothesis, not a proven full cycle.

The standard test now explicitly protects the complete dependency set of this small
fixture in the source phase before boundary reads and writer start. It retains the
same query shape, Serializable isolation and integrity checks. This is a stronger,
complete-read-set experimental variant, not evidence that the original partial
window was safe or that all future window transitions are deadlock-free.

The original two-key schedule is retained as
PartialBoundaryWindowReproducesOriginalUnresolvedSchedule. To gather evidence,
independently opt in on the same guarded disposable clone:

```powershell
$env:FACTORYCONNECT_GG003_D1A1_REPRODUCE_PARTIAL_WINDOW = '1'
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~PartialBoundaryWindowReproducesOriginalUnresolvedSchedule' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=gg003-D1A1-original-partial.trx' --results-directory artifacts/TestResults/GG003-D1A1
Remove-Item Env:FACTORYCONNECT_GG003_D1A1_REPRODUCE_PARTIAL_WINDOW
```

This remains a no-deadlock assertion, not an expected-deadlock passing test. Preserve
its failure and matching graph. Even a passing complete-read-set run does not close
the unresolved original partial-window feasibility finding.

The original timeout test sent SET LOCK_TIMEOUT through ExecuteAsync, which attaches
stream parameters even when unused. SET options inside sp_executesql can revert when
that dynamic batch returns (Microsoft Learn, SET Statements). The old TRX contains
no @@LOCK_TIMEOUT readback, so RPC scoping is the source-supported explanation to
verify, not a directly measured fact from that run.

The revised test uses parameter-free SET/SELECT commands, sets the timeout after
transaction begin, asserts @@LOCK_TIMEOUT=0 in a separate command immediately before
the actual verifier, preserves the 1222 expectation and restores/read-checks the
original session value. Neither verifier CommandTimeout nor production settings are
changed. A remaining -2 is still a failure; do not broaden the assertion.

Reference: https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statements-transact-sql?view=sql-server-ver17

## Targeted rerun order

Rebuild first. With the guarded D1-A1 variables set, run the two revised cases
individually before repeating the complete-scope suite:

```powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~ActualVerifierPropagatesCancellationAndSqlLockErrorsInBothScopes' --logger 'trx;LogFileName=gg003-D1A1-timeout.trx' --results-directory artifacts/TestResults/GG003-D1A1
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~ProtectedPredicatesRemainStableThroughRepeatedActualVerification&DisplayName~boundary' --logger 'trx;LogFileName=gg003-D1A1-complete-boundary.trx' --results-directory artifacts/TestResults/GG003-D1A1
```

Confirm each filter actually executes one test, not zero. Preserve the original
21/23 TRX and its plans alongside the revised and original-reproducer results.
No local SDK/SQL runtime is available here; none of these reruns has been performed.

Original gg003-D1A1.trx SHA-256: d65394f121901716479029ad5de4cf308abe9103d89b665668f51304dcc84ab3.
