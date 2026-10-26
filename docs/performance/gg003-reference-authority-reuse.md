# Reporting-only reference-time authority reuse

Base: `df1bbac1c0bb5ab26d43af6d71d92c78d28bb9d2` (PR #161).

## Contract and boundary

The existing reference-time validator returns `VerifiedReferenceTime`: a canonical period-specific authority binding and a nullable duration aggregate. Its successful return follows all membership, transition, outcome, source/standard, canonical consistency and completeness checks. A null aggregate represents missing/unresolved period reference time; a successful SQL read alone is not a reusable result.

`SqlContainmentReportingReferenceTimeScope` is allocated only in `SqlContainmentVerifiedRead.TryReadAsync`. It is passed through the historical-read scope to the unchanged validator logic. Other callers retain uncached validation, including archive/publication preparation. No schema, isolation, gate, lock, timeout, or acquisition production changes are included.

The identity contains the complete `MetricAggregationCheckpoint` (aggregation processor, machine/stream and position), the exact `OperationalMetricPeriodId` (including full shift/day identity), and fixed `AvailabilityContainmentV1.PolicyId`. The scope owns one connection and Serializable transaction. Reference identity, open/stable transaction state and cancellation are checked before any hit; ownership/cancellation are checked again after loading and before admission. Concurrent scope use is rejected. No scope may be supplied to a transaction that writes reference-time authority.

Only fully validated results with a non-null duration are admitted. Failures, corruption, cancellation and missing/unresolved duration results are never admitted. An OEE root unavailable because of Availability can still use valid Performance authority: this cache does not hold OEE/Performance evaluation outcomes. Binding collections and duration values are immutable. Every root still evaluates independently and every retained envelope still undergoes its own canonical byte comparison; no root evaluation cache is introduced.

Successful containment-verification completion logs expose:

- `RootReconstructionInvocations`: every root reconstruction in the reporting scope.
- `ReferenceTimeAuthorityValidationExecutions`: actual loader attempts, excluding cache hits.
- `ReferenceTimeAuthorityCacheHits`: successful decision reuse.

`historical.reference-time.validate` now surrounds actual validation executions, not memoized accesses. Counters are attached to the completed `containment.publication.verify` span. They are not cross-request totals. Failed requests must be inspected using their error and span evidence; the final success counter record may be absent.

## Important GG-003 benchmark limitation

The supplied `PR161-profile-api.log` contains four requests. All 312 stopped `historical.reference-time.outcomes.read` spans (78 per request) report `InputCount=0`. Source completeness rules return no duration when produced-source membership is empty, and reject missing required outcomes otherwise. Thus these successful zero-outcome validations are expected to return null duration.

Under the approved rule excluding unavailable/insufficient results, this implementation is expected to yield **zero cache hits for that recorded state**. It must not be represented as a demonstrated or expected latency improvement for that dataset. Confirm the actual runtime counters against the disposable clone. If they remain 195 roots / 78 executions / 0 hits, that is faithful enforcement of the safeguard, not performance acceptance.

A subsequent design could consider independently validated completed-cut authority evidence while deriving missing period results anew. That is a different admission contract; it is not implemented here. Do not silently admit null-duration results to obtain benchmark hits.

Baseline from the attached four requests: median `reporting.query` 27.17044165 s; median ASP.NET request duration 27.17585265 s. Root reconstruction count 195/request; reference-time validation count 78/request; contribution SQL execution count 20/request. Stages overlap. Started reconstruction tags may inherit the outer publication cut before local tags are assigned; use stopped-span exact-cut tags.

## Validation preparation

No .NET SDK or Windows LocalDB is available in the editing environment. `dotnet --version` fails with command not found. Compilation, tests and real-data performance are pending. Whitespace checking alone is not correctness acceptance.

On the developer machine use an isolated checkout, SDK 10.0.402, and record actual commit/build/configuration. Run:

```powershell
dotnet build FactoryConnect.sln -c Release
if ($LASTEXITCODE) { throw 'Build failed' }
dotnet test tests/FactoryConnect.Core.Tests -c Release --no-build --logger 'trx;LogFileName=PR162-Core.trx'
if ($LASTEXITCODE) { throw 'Core tests failed' }
$env:FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true'
dotnet test tests/FactoryConnect.Integration.Tests -c Release --no-build --filter 'FullyQualifiedName~SqlContainment|FullyQualifiedName~SqlServerOperationalMetricProjection|FullyQualifiedName~ProductionDayShiftOperationalMetricEndpointTests|FullyQualifiedName~SqlServerProductionDayShiftOperationalMetricConformanceIntegrationTests' --logger 'trx;LogFileName=PR162-SQL.trx'
if ($LASTEXITCODE) { throw 'SQL regressions failed' }
dotnet test tests/FactoryConnect.Edge.Tests -c Release --no-build --logger 'trx;LogFileName=PR162-Edge.trx'
if ($LASTEXITCODE) { throw 'Edge regressions failed' }
```

The selected SQL fixtures create/drop uniquely named disposable databases. Use only the developer SQL instance; never a production connection.

New SQL coverage exercises paired Performance/OEE with calculated/unavailable roots and byte equivalence, forged canonical envelopes after warming, different periods/sources/revisions, missing/ambiguous/empty reference-time results, corruption/failure/post-load cancellation admission, cancelled hits, foreign owner rejection and committed/rolled-back/disposed owner rejection. An actual reporting verifier test checks five roots, one authority validation and one hit on a resolved five-metric fixture. Existing acquisition/publication regressions remain necessary.

For the real-data comparison follow the target/schema/payload guards in `gg003-multi-shift-reporting.md`: confirmed disposable developer restores only, current migration ledger/checksums through 022, exact executable and effective SQL target, same verified 16 October request, complete parsed JSON comparison. Never use Gajra, a protected reference database, or `GG003_ACQ_A2_20261026`.

Use the same four-request protocol for base and candidate, preserving warm-up labels and each raw server duration. Enable `ReportingDiagnostics:Enabled=true` with Information timing logs. Report roots, actual authority validation executions, hits, verification/transaction/query/ASP.NET durations and response equality separately. Capture errors and cancellation; do not increase timeouts. Concurrent acquisition/reporting validation is a separate disposable-clone phase and remains pending.

Keep draft. No improvement is accepted without measured lower median and semantic equivalence; zero eligible reuse in GG-003 requires a revised design review, not weakened admission. Do not merge, deploy, or poll CI.
