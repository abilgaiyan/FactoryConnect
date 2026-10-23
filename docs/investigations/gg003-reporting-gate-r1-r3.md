# GG-003 reporting gate contention: offline R1–R3 probes

Baseline source: cada09dc056f6f9cfaa422d0af077140d053ca91.
This branch adds test code only. It changes no production timeout, isolation, gate scope, configuration or service lifecycle.

## Status
Prepared, not executed in the editing environment (no .NET SDK / SQL restore access).
R1–R3 are not closed by creating this harness. Production attribution requires measured evidence.
Run the known-processor test first; review its output before running the restore probe.

## R1 baseline
Resource: FactoryConnect:OperationalMetricProjection:Processor:v1: plus uppercase SHA256 of StringOrderKeyV2Codec.Encode(exact processor ID).
Gate timeout: 15000 ms; gate command timeout: 20 seconds.
Shared readers and exclusive publishers use transaction-owned application locks.
Legacy reads use ReadCommitted; containment reads and both publisher paths use Serializable.
The shared gate spans verification/materialization to transaction completion.
The exclusive gate spans publication preparation/verification and checkpoint publication to transaction completion.
ReadPeriodSummariesAsync obtains the complete retained publication before filtering the requested period.

## Stage 1: known-processor mechanism reproduction
Use an existing LOCAL DEVELOPER SQL integration-test connection environment:
FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING.
The fixture creates and drops its own temporary database on that SQL instance.

Run from the repository root in PowerShell:
~~~powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~ReportingGateContentionBaselineTests' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=reporting-gate-baseline.trx' --results-directory artifacts/TestResults/ReportingGateBaseline
~~~

The test pauses a real format-2 publication at the existing ReferencesInserted test hook.
It waits for the real summary reader's DEFAULT 15-second gate timeout, then releases the publisher.
That deliberate pause proves the mechanism; it MUST NOT be counted as naturally slow publication.
After release it verifies exact processor/source/result keys, unchanged exact replay, and another processor's independence.
A missing SqlClient diagnostic gate event fails the test rather than silently reporting an invented duration.

## Stage 2: disposable GG-003 restore
Prepare a separate Windows LocalDB clone named FactoryConnect_GG003_GATE_RCA_<suffix>.
Never point this probe at Gajra or the original RCA database.
The probe enforces the LocalDB prefix and database-name prefix before reading publication tables.
It does not restore/create/drop a database, migrate, or advance the runtime.
Use a current-schema clone with an existing complete publication for the EXACT processor used by the report.
Do not substitute containment for a legacy processor, or vice versa, just to make the probe work.
The identity must bind machine 677b030b-1ede-4a67-82ae-6a494791357f.

Supply through the local process environment:
- FACTORYCONNECT_GG003_GATE_RCA_CONNECTION_STRING: connection to that disposable clone
- FACTORYCONNECT_GG003_GATE_RCA_PROCESSOR: exact processor from the reporting configuration
- FACTORYCONNECT_GG003_GATE_RCA_DATE: requested business date, yyyy-MM-dd

Do not start other writers against the clone for this first isolated replay measurement.
Run:
~~~powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~Gg003ReportingGateRestoreMeasurements' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=gg003-reporting-gate-restore.trx' --results-directory artifacts/TestResults/Gg003ReportingGateRestore
~~~

Missing opt-in settings SKIP this test. A skipped test is NOT acceptance.
The probe supports formats 1 and 2 and uses each format's actual exact-replay publication path.
It replays the complete latest batch, never a partial date batch.
Format-2 stage callbacks reject any unexpected mutation; publication snapshots must remain identical.
It starts the requested Production Day summary read after observing the writer's gate command complete.
Scheduling may still let a short replay finish before the reader requests its gate.
A call-overlap marker alone does NOT prove lock contention. Inspect command timings and reproduce further if needed.
The business-date probe uses the unpartitioned context; it fails if that date/context has no published results.

## Measurements and limits
- SQL gate-command elapsed time is recorded separately for reader/writer/verification connections.
  This includes SQL execution and network overhead; it is not a pure SQL lock-wait statistic.
- Publication call elapsed includes preflight, connection setup, transaction work, commit and disposal.
- exclusiveGateToCallReturnUpperBoundMs is an UPPER BOUND, not an exact lock-held duration.
  It ends at method return because no production transaction instrumentation was added.
- SQL execution groups: evidence, result archive, witness archive and other SQL.
  Each command belongs to one group. Execute return does not include all row streaming / client CPU.
- baselineWholePublicationReadMs includes the real stable-read path and retained verification.
- standaloneWholePublicationVerificationMs includes all archive/evidence/historical verification.
- standaloneOneResultHistoricalReconstructionMs independently repeats reconstruction of one canonical latest-batch result.
  It is a separate probe; do NOT subtract it from verification or treat it as the whole publication's reconstruction cost.
- Successful results must preserve summary identities/values/reasons, unchanged checkpoint and complete publication/archive snapshot.
- Format-1 publication has no containment reconstruction measurement.
- This is an in-process SQL reporting probe, not an HTTP endpoint test.

## Remaining R2/R3 work after these outputs
Exact replay and advancing publication can have different costs. Do not infer advancing-write performance from replay.
If the baseline/probe implicates retained verification, instrument a separate controlled advancing-runtime run on a fresh clone.
To attribute the production symptom fully, capture actual gate grant/release and downstream wait timing for that run.
These probes do not identify the former live blocker or prove a link to MachineObservation lock waits.
Review timings before selecting R4. No timeout/gate-scope changes, restart, deployment or database correction is authorized.
