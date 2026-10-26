# D1-A1 diagnostic harness: execution and coverage

Implementation status: test-only harness added; local compilation and SQL execution
pending. No D1-A1 measured results or production consistency conclusions are claimed.
PR #159's files and reported D1-C evidence remain unchanged.

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

## Run the 23 opt-in cases on the separate clone

```powershell
$env:FACTORYCONNECT_GG003_D1A1_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=GG003_ACQ_D1A1_20261026;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:FACTORYCONNECT_GG003_D1A1_DISPOSABLE_DATABASE = 'GG003_ACQ_D1A1_20261026'
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~Gg003ProtectedReadFeasibilityTests' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=gg003-D1A1.trx' --results-directory artifacts/TestResults/GG003-D1A1
Remove-Item Env:FACTORYCONNECT_GG003_D1A1_CONNECTION_STRING
Remove-Item Env:FACTORYCONNECT_GG003_D1A1_DISPOSABLE_DATABASE
```

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
transaction IDs. Lock snapshots include modes, status and resource identifiers.
The same holder connection polls for actual blocking, then releases its transaction;
no arbitrary sleeps coordinate actors. Missing required waits fail. SQL command
bounds and wait deadlines stop unsuccessful schedules. Deadlocks/timeouts remain
failures; no exception is accepted in place of required evidence.

## Coverage matrix

| Cases | What runs | Required evidence |
| --- | --- | --- |
| Existing predicates (6) | Content update, source delete, instance/sequence/position update, deletion of unreferenced historical boundary | Writer waits; actual verifier executes twice before protection release; mutation then completes and fresh exact read is Corrupt |
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
reader, not an HTTP reporting request. No deadlock graph collection or deliberately
induced deadlock schedule is implemented; any observed SQL deadlock fails the run.

No synthetic result replaces the preserved 81.31-second append reference. Real-history
performance, HTTP reporting contention, all mid-verifier mutation schedules, oversized
window state and optimized-path acceptance equivalence remain unverified. Preserve
TRX/plan output and review all coverage gaps before any D1-A2 proposal. No merge or
production deployment is authorized by this harness.
