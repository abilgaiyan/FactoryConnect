# D1-C acquisition consistency experiments

Status: D1-C execution reported PASS on 26 October 2026: 8 passed, 0 failed,
12.58 seconds. Results below are transcribed from the operator's supplied summary;
the raw D1-C TRX was not independently inspected in this documentation update.
PR #159 remains draft. D1 production optimization remains unimplemented and unapproved.

## Preserved reference

The existing `Gg003AcquisitionCloneBenchmarkTests.cs` and its execution instructions
are unchanged. Preserve the advanced A2 clone (ordinal 40,546), successful TRX and
`f963fff` benchmark: 40,545 predecessor publications, 95,274 source commands,
180.36-second baseline exact read, 81.31-second append transaction and 45.10-second
replay. Do not rerun that benchmark as part of these experiments.

## Target and data boundary

Restore a **separate disposable LocalDB clone** as `GG003_ACQ_D1C_20261026` from the
original, unadvanced baseline. Keep services disconnected from it. The guard accepts
only this exact catalog on a developer LocalDB instance, rejects attached files and
requires a separate exact-name acknowledgement. It checks the restored GG-003 head
is at ordinal 40,545, instance 1792061301 and next sequence 482204 before seeding.

Each test creates a new random machine and a `d1c:` stream through the real store.
Only these synthetic rows are updated/deleted. No restored GG-003 observations,
publications, schema, constraints, isolation settings or migrations are changed.
Synthetic test data is retained for inspection; restore a fresh clone before a new
acceptance run. The insert-conflict experiments intentionally leave an extra raw
fixture row without a publication; these streams are not acquisition workloads.
The advanced A2 clone name is never accepted.

## Compile and discover before executing SQL

From the PR #159 branch in a clean developer worktree:

```powershell
Remove-Item Env:FACTORYCONNECT_GG003_D1C_CONNECTION_STRING -ErrorAction SilentlyContinue
Remove-Item Env:FACTORYCONNECT_GG003_D1C_DISPOSABLE_DATABASE -ErrorAction SilentlyContinue
dotnet restore tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj
dotnet build tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-restore
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --list-tests --filter 'FullyQualifiedName~Gg003AcquisitionConcurrencyTests'
```

These commands do not execute the SQL scenarios. Compilation failures must be fixed
before enabling the opt-in variables. The implementation environment has no `dotnet`
or SQL Server, so this compile gate has not been executed here.

## Execute the eight controlled cases

```powershell
$env:FACTORYCONNECT_GG003_D1C_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=GG003_ACQ_D1C_20261026;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:FACTORYCONNECT_GG003_D1C_DISPOSABLE_DATABASE = 'GG003_ACQ_D1C_20261026'
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~Gg003AcquisitionConcurrencyTests' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=gg003-acquisition-D1C.trx' --results-directory artifacts/TestResults/GG003-D1C
Remove-Item Env:FACTORYCONNECT_GG003_D1C_CONNECTION_STRING
Remove-Item Env:FACTORYCONNECT_GG003_D1C_DISPOSABLE_DATABASE
```

The test class uses the existing nonparallel clone-test collection. Within a scenario,
A and B are the two SQL actors. Diagnostics are queried on the paused holder's owning
connection/transaction, rather than opening a third concurrent observer connection.
The store itself owns its real transaction; test listeners do not replace it.

| Cases | Schedule | Required outcome |
| --- | --- | --- |
| Default transaction, update/delete (2) | A takes ingestion's checkpoint lock and fully reads interior X; B commits modification; A rereads | Actual isolation=ReadCommitted; reread differs; real exact reader returns Corrupt, replay throws InvalidOperationException, append throws InvalidDataException |
| Actual exact reader, update/delete (2) | Pause actual reader after source verification, before transaction commit; B mutates X | Actual isolation=Serializable; B has observed LCK_M wait blocked by A; release A, read returns Found, B completes; next exact read returns Corrupt |
| Conflicting identity/position insert (2) | A inserts new synthetic row uncommitted; B inserts conflicting key/position | B waits on A; after A commits, B gets 2601/2627 naming the expected constraint |
| Referenced endpoint delete (1) | A confirms contact authority references position 3; B deletes it | SQL 547; distinguish this from unreferenced interior deletion |
| Actual same-stream ingestion (1) | Pause A after real raw-state writes; start B with stale expected checkpoint | Actual A isolation=ReadCommitted; B waits on A; after A commit, B rejects stale checkpoint; winner checkpoint/evidence verified |

The two default-transaction cases model the retained-value versus fresh-value
counterexample and then exercise actual store corruption handling. They do **not**
pause production verification between two publication lookups. No optimized algorithm
exists here, so a baseline-versus-optimized acceptance comparison is not reported.

## Coordination and diagnostics

Existing ActivityStopped events provide deterministic test-only pauses after SQL
readers have closed: `acquisition.evidence.sources.verify` for the exact reader and
`acquisition.raw-state.write` for ingestion. A bounded ManualResetEventSlim gate holds
the actor until the test releases it; it is released in finally blocks. The contender
must be observed in `sys.dm_exec_requests` waiting on the holder. Bounded DMV polling
with scheduler yields is used, not arbitrary sleeps. Failure to observe the required
wait fails the test; it is not counted as successful protection.

Output includes timestamped holder/session transaction identities, effective SQL
isolation, client isolation, SQL version, RCSI and snapshot options; waiter/blocker
IDs, waiter transaction/isolation, wait type/resource and sampled wait milliseconds;
plus explicit mutation/commit/validation ordering and evidence outcomes. A wait sample
is not total lock-hold duration or reporting latency. SQL exceptions, timeout and
cancellation remain test failures, not substitutes for an expected corruption result.
The experiments require DMV permissions on the disposable instance.

## Existing corruption regression mapping

| Contract | Existing test |
| --- | --- |
| Altered hash / unsupported version | `CorruptionAndUnsupportedEncodingHaveExplicitResults` |
| Missing/content/identity/position damage | `DeletedOrAlteredPredecessorObservationsInvalidateHistoricalPublicationAndReplay` and `CorruptionInTheOldestChunkRejectsLatestReadReplayAndAppendAtomically` |
| Invalid enums: Corrupt read, InvalidDataException replay/append | `InvalidPersistedEnumsKeepExactReadAndCommitExceptionClassifications` |
| Exact machine/stream/instance separation | `ExactMachineStreamAndInstanceIdentitySurvivesCollidingSequences` |
| Repeated observations and positions | `DuplicateOccurrencesAndReplayedPositionsAreComparedWithoutAllocatingAgain` |
| SQL insert-stage atomic rollback | `FailureAtEachInsertStageRollsBackObservationsCheckpointAndContact` |
| Cancellation / restart | `CancellationDuringPredecessorVerificationRollsBackAllFiveAcquisitionStructures` |
| Replay and competing writers | `ConcurrentIdenticalReplayIsIdempotentAndCompetingPublicationLosesCas` and `CompetingLongHistoryWritersPublishExactlyOneSuccessor` |

## Evidence still required

The operator reports all eight cases passed. Preserve the D1-C TRX with the actual
build revision, SQL version, database options and session/transaction evidence.
Unexpected incidental blocking must not be interpreted as general immutability.

## Reported D1-C outcome

| Scenario | Reported result | Interpretation |
| --- | --- | --- |
| Actual Serializable reader: update/delete | 2/2 PASS; LCK_M_X | Mutations wait for the existing exact-reader transaction to finish |
| Default transaction: update/delete | 2/2 PASS; modification commits before reread | Earlier retained values are not generally stable; actual subsequent exact read is Corrupt, replay throws InvalidOperationException and append throws InvalidDataException; checkpoint unchanged |
| Conflicting identity/position insert | 2/2 PASS; SQL 2627 | Existing unique constraints reject conflicts |
| Referenced boundary deletion | 1/1 PASS; SQL 547 | Existing foreign key protects the referenced endpoint |
| Same-stream ingestion | 1/1 PASS; LCK_M_U | Contender waits, then rejects stale checkpoint; winning publication reads successfully |

These are controlled synthetic streams on the restored clone, not concurrent mutations
of the full GG-003 historical chain. No optimized retrieval path was executed.

## Proposed implementation boundary after D1-C

Option A can be narrowly scoped to the existing exact-reader Serializable transaction.
Retain the same owning connection and transaction until every covered occurrence has
been validated; discard lookup buffers at window completion, and never export verified
authority to another invocation. Keep 128-key source/boundary queries, 128-publication
windows and the existing oversized-publication validation state. This is a proposal,
not implementation authorization or a measured performance result.

Acquisition append and replay must retain the established per-publication source and
boundary retrieval under their existing transaction behavior. Do not enable shared
cross-publication lookup buffers there merely because the transaction owns checkpoint
or head locks. Boundary deduplication has the same visibility qualification and is not
silently included as an independent safe optimization.

Optimizing exact reads alone will not resolve the measured 81.31-second append
transaction. To optimize append/replay, a separately reviewed consistency protection
or a demonstrated equivalent retrieval design remains necessary. No isolation, lock
hint or immutability change is authorized by D1-C.

Before a restricted exact-reader implementation is accepted, run the same concurrency
schedules against that implementation, verify absence/duplicate/enum classifications,
per-occurrence comparisons and oversized-window state, then compare command counts,
workspace bounds and timings. Separately measure real-history concurrent reporting.

D1 also needs the enforceable consistency decision. These tests do not establish
safety for all interleavings, cross-publication reuse, mid-verification mutations in
the actual append/replay path, oversized-window state, or a new optimizer's exception
precedence. They do not measure real-history concurrent reporting performance. Those
remain separate gates. No merge or Gajra deployment is requested by these instructions.
