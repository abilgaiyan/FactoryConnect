# D1-C acquisition consistency experiments

Status: implemented as opt-in diagnostic tests; compilation and SQL execution are
pending. No measured D1-C results are recorded yet. PR #159 remains draft. This is
not a production retrieval implementation and does not close D1.

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

All eight new SQL cases are unexecuted in this implementation session. Preserve the
TRX and review effective options and observed resources before accepting their results.
Unexpected incidental blocking (FK, page/table escalation, different access plans)
must be investigated rather than interpreted as general immutability.

D1 also needs the enforceable consistency decision. These tests do not establish
safety for all interleavings, cross-publication reuse, mid-verification mutations in
the actual append/replay path, oversized-window state, or a new optimizer's exception
precedence. They do not measure real-history concurrent reporting performance. Those
remain separate gates. No merge or Gajra deployment is requested by these instructions.
