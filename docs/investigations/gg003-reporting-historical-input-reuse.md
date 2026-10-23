# GG-003 reporting recovery: transaction-scoped historical inputs

Status: source correction prepared for review; SQL validation and release are pending.

Base: `cada09dc056f6f9cfaa422d0af077140d053ca91` (`main`). This recovery branch
is independent of draft diagnostic PR #151. R3.1B remains paused.

## Problem and correction

The accepted offline evidence showed five historical-contribution executions taking
126.95 seconds during an advancing publisher's Exclusive gate ownership. A concurrent
reporting read timed out. That establishes harmful repetition during gate ownership;
it does not establish why an individual SQL execution is slow.

Source inspection identifies repeated contribution reads across retained-result archive
verification, result reconstruction, and reference-time verification. Reporting's initial
checkpoint read also verifies the complete retained format-2 publication.

`SqlContainmentHistoricalReadScope` reuses only fully loaded, independently validated
historical inputs. Its first access calls the unchanged
`SqlContainmentHistoricalContributionReader.ReadAsync`, including processor/stream
binding, exact revision, membership, fact materialization and aggregate integrity checks.
Only successful completion is admitted; SQL exceptions, integrity failures and cancellation
retain their identities and do not populate the cache. The collection is copied into an
owned read-only wrapper before admission. Fact and identity objects are immutable records.

The key is the value-equal `MetricAggregationCheckpoint`: aggregation processor plus
stream (machine and exact stream key) plus position. Case-distinct strings remain distinct.
There is no latest-revision substitution or prefix reuse between revisions 534 and 537.

## Ownership and affected callers

| Caller | Lifetime and use |
| --- | --- |
| `SqlContainmentVerifiedRead.TryReadAsync` | A fresh scope per verification attempt, shared across retained archive verification and subsequent independent result reconstruction. |
| `SqlServerOperationalMetricContainmentPublisher.PublishAsync` | One scope in its preparation transaction, shared across proposed projections. |
| `SqlContainmentPublicationTransaction.PublishAsync` | A different scope created after Exclusive acquisition, shared across retained and incoming archive verification. |
| `SqlContainmentArchiveTransaction.PrepareAsync` | Accepts the owner's scope; direct callers receive a fresh scope. All existing archive locks, comparisons and DML ordering remain. |
| `SqlContainmentHistoricalResultValidator` | Re-evaluates every key independently; nested component reads and reference-time verification use the same input scope. |
| `SqlContainmentReferenceTimeAuthorityValidator` | Reuses source membership only. Period-specific A/R/S authority and reference-time decisions are reconstructed on every invocation. |

The availability-only authority seam has no production caller in the SQL persistence
assembly and is unchanged. Public interfaces and the legacy format-1 path are unchanged.

The scope checks reference identity of the SQL connection and transaction at entry, and
requires the same open Serializable transaction on cache hits as well as misses. A completed
transaction cannot serve cached data. It does not own or dispose the caller's SQL resources.
Overlapping scope reads are rejected rather than issuing concurrent SQL on that transaction.

These scoped callers write containment publication/archive tables only. They must never
write source facts, contribution membership or aggregation revisions while using a scope.
Serializable locks retained by the first load protect the validated range from concurrent
writers; transaction-local writes to that range are outside this scope's contract.

## Preserved invariants and limitations

- Every result is independently evaluated and canonically compared. No metric result,
  witness conclusion, reference-time authority decision or period aggregate is cached.
- Preparation and atomic publication have separate connections, transactions and scopes.
  Reporting attempts and requests also have separate scopes. No static or ambient cache exists.
- Exclusive/Shared gate modes, acquisition order, transaction isolation, 15-second gate timeout,
  SQL query text, indexes, migrations, checkpoint publication and rollback are unchanged.
- Exact replay still checks the complete immutable archive and retained publication. Incoming
  claims never supply trusted historical inputs. There is no legacy fallback or repair.
- The scope retains one collection per distinct requested cut until the operation ends.
  Large retained publications can therefore increase peak memory; measure this during validation.
- A single historical SQL query can still be slow. Reduced execution count is not proof of
  recovered report latency or of the SQL latency's underlying cause.

## Validation

The database-free `ContainmentHistoricalInputReuseTests` cover exact-cut reuse, all four
identity dimensions, owned/read-only membership, separate caches, failure retry,
cancellation before access and after loading, and overlapping-use rejection.

The prepared SQL tests in `SqlContainmentHistoricalReadScopeIntegrationTests.cs` extend
the existing historical-result fixture. They cover canonical equivalence across every built-in
definition and both period types, forged claims after warming, transaction/connection mismatch,
completed owners, weak isolation rejection and corrupt first-load evidence.

After separate SQL authorization, run those tests and existing historical authority/result,
archive, publication and verified-read integration suites. Those existing suites exercise
exact replay, corruption rejection, every publication rollback stage, concurrent publication,
source advancement and coherent reads through the modified production callers.

Build and database-free validation only:

```powershell
dotnet build tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed; tests stopped.' }
dotnet test tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj `
  -c Release --no-build `
  --filter 'FullyQualifiedName~ContainmentHistoricalInputReuseTests&Category!=SqlServerIntegration'
if ($LASTEXITCODE -ne 0) { throw 'Database-free tests failed.' }
```

Review commits use `[skip ci]`: the repository's ordinary PR workflow also starts SQL
integration jobs, which are not authorized for this correction yet. This does not waive
those checks before merge/release; execute them only after explicit approval.

### Preparation results

- .NET SDK 10.0.402, Linux: integration-test project Release build passed with analyzers enabled.
- Database-free input-reuse tests: 14/14 passed.
- SQL integration tests: prepared, not executed. Their compilation is covered by the build.
- Independent code review, fresh-restore measurements and release acceptance: pending.

## Fresh-restore acceptance (not authorized for execution)

1. Use a newly named disposable revision-534 clone; leave all preserved revision-537 clones unchanged.
2. Establish complete legitimate advancing-batch provenance and record exact processor/stream/cuts.
3. Observe no more than one contribution-reader load per distinct cut per scope. Preparation
   and publication must each perform their own first load; reporting must perform its own load.
4. Compare canonical output, witness/reference bindings and complete manifests against accepted
   evidence. Confirm coherent advancement, exact replay without DML and failure rollback.
5. Measure whole reporting latency, historical command count/duration, Exclusive acquisition-to-commit
   duration, concurrent Shared acquisition, and peak memory. Keep workload success, latency acceptance
   and diagnostic coverage outcomes separate.
6. If latency remains unacceptable after repetition is removed, investigate the individual SQL
   access path separately. Do not infer an index or gate-scope correction from this change.

No database execution, service restart, production deployment or Gajra change is part of
this implementation. Reports remain unrecovered until separately validated and released.
