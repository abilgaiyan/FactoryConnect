# GG-003 reporting gate contention: offline R1–R3 probes

Baseline source: cada09dc056f6f9cfaa422d0af077140d053ca91.
This branch adds test code only. It changes no production timeout, isolation, gate scope, configuration or service lifecycle.

## Status
R3.1B read-only DMV probe is CODE/BUILD REVIEW ONLY. Do not run another SQL measurement until separately authorized.
Preserve both advanced clones at revision 537; no measurement rerun is authorized by the commands below.

At f49122e8, the developer reported both opt-in tests passing in separate local runs:
- Known processor: deliberate default gate timeout reproduced; coherence/replay/isolation assertions passed.
- Stage-B GG003 format-2 clone at revision 534: exact replay and unchanged-state assertions passed.
  Exclusive acquisition 5.7341 ms; gate-to-call-return upper bound 4684.445 ms;
  concurrent shared gate command 4649.4118 ms; no reporting failure.
  Baseline whole-publication read 7825.2076 ms; standalone verification 7343.16 ms;
  independent one-result reconstruction 64.5656 ms. 25 retained summaries, 5 for 2026-10-15.
The earlier skipped execution is separate and is not measurement evidence.
R1/R2A/R2B accepted. At 9be8d64f the developer also reported build/baseline PASS and a separate
advancing probe PASS: actual next batch 534 -> 537, publisher 3631.7697 ms, gate-to-commit-return
upper bound 2697.1636 ms, commit 2.2984 ms, shared command 2654.4003 ms, concurrent reporting
10656.2289 ms. DMV samples identified report session 80 waiting LCK_M_S behind publisher 79.
These are accepted clone results, not production attribution. Preserve that revision-537 clone.
Nine initial-checkpoint other-sql commands totalled 213288.1893 ms before sampler startup.
The follow-up below addresses that coverage gap; it has not been compiled or run in the editing
environment (no .NET SDK / SQL restore access). R3 attribution remains incomplete.

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


## Stage 3: one legitimate advancing publication (R3B/R3C)

Use a NEW clone named FactoryConnect_GG003_GATE_ADVANCE_<suffix>, separate from the accepted
GATE_RCA clone. The test requires the starting containment checkpoint to be exactly 534.
Do not run other writers on this clone. After any attempted run, treat the clone as consumed:
restore another fresh clone before repeating; do not reset its checkpoint.

The existing successful copy-only Stage-B backup can be restored to this separate database.
For the confirmed developer environment, in PowerShell:

~~~powershell
$gateAdvanceBackupPath = 'F:\Development\FactoryConnect\TestResults\GateRca\GG003_StageB_GateRca_20261023_215044.bak'
$gateAdvanceDirectory = 'F:\Development\FactoryConnect\TestResults\GateRca'
if (!(Test-Path -LiteralPath $gateAdvanceBackupPath)) { throw 'Stage-B backup not found.' }
$gateAdvanceSql = @"
IF DB_ID(N'FactoryConnect_GG003_GATE_ADVANCE_20261023') IS NOT NULL
    THROW 50001, 'Advance clone already exists; restore stopped.', 1;
RESTORE DATABASE [FactoryConnect_GG003_GATE_ADVANCE_20261023]
FROM DISK = N'$gateAdvanceBackupPath'
WITH MOVE N'FactoryConnect'
    TO N'$gateAdvanceDirectory\FactoryConnect_GG003_GATE_ADVANCE_20261023.mdf',
     MOVE N'FactoryConnect_log'
    TO N'$gateAdvanceDirectory\FactoryConnect_GG003_GATE_ADVANCE_20261023_log.ldf',
     RECOVERY, CHECKSUM;
"@
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d master -b -Q $gateAdvanceSql
if ($LASTEXITCODE -ne 0) { throw 'Clone restore failed; do not run the probe.' }

git pull --ff-only
$env:FACTORYCONNECT_GG003_GATE_ADVANCE_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect_GG003_GATE_ADVANCE_20261023;Integrated Security=true;TrustServerCertificate=true'
$env:FACTORYCONNECT_GG003_GATE_RCA_DATE = '2026-10-15'
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~Gg003ReportingGateAdvancingMeasurements' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=gg003-gate-advance.trx' --results-directory artifacts/TestResults/Gg003ReportingGateAdvance
~~~

This test selects the next ACTUAL aggregation revision after the durable checkpoint; it does not
assume that positions are consecutive. It uses CoherentOperationalMetricEvaluationBatchSource,
OperationalMetricProjectionProcessingRuntime, the historical reference-time convergence prerequisite,
and the real SqlServerOperationalMetricContainmentPublisher. The publisher decorator only times and
captures the complete batch generated by the runtime; it does not change envelopes or commit behavior.
Missing next source revision fails the test rather than synthesizing a batch.

Only the new clone's reference-time prerequisite state and containment publication are allowed to
advance through the real contracts. Source facts, contribution ledger, source checkpoint, revision
inventory/period identities and legacy publication must fingerprint identically before and after.
The proposed checkpoint/manifest must match durable readback, and every generated projection's
summary identity, revision, status, value, unit and reasons must match reporting readback.
The concurrent period read must match the post-publication retained period, or report its gate timeout.

### Independent boundaries
- Phase timers: baseline whole-publication read, reference-time prerequisite, complete next-batch
  evaluation, actual containment publisher, full runtime cycle, concurrent period read, and post-read.
- Publisher diagnostics: gate commands, checkpoint read/write commands and transaction commit
  before/after events. Session ID, client connection ID, transaction ID, operation ID and monotonic
  timestamps correlate commands with DMV samples. Commit events for the exclusive-gate transaction
  are required; missing correlation fails acceptance rather than claiming an exact duration.
- Gate completion to commit return remains an upper bound; it is NOT an exact server grant/release trace.
  Publisher preparation has a separate transaction; do not confuse its commit with publication commit.
- Every retained envelope is independently probed with archive PrepareAsync(requireExisting:true,
  verifyOnly:true) and ReconstructVerifiedAsync in a shared-gated Serializable transaction.
  Each has its own elapsed timer and result index. Archive preparation itself includes historical
  validation, so these independent probes overlap in work and MUST NOT be added/subtracted as
  disjoint internal stages of the earlier real reporting call.
- Test-only command diagnostics report group totals, individual checkpoint/commit events and SQL
  commands taking at least 100 ms, without printing raw SQL or parameter values.
- A separate monitor samples requests, session transactions and APPLICATION locks every nominal
  250 ms during the runtime advance and overlapping report. It records wait type/time, blocker,
  request/session transaction IDs, lock owner/mode/status, connection ID and timestamp.
  Sampling starts before the INITIAL checkpoint read and continues through all readback/verification; readiness/DMV errors fail the test. Stop/read diagnostics are
  observed even if publication or reporting fails.
- Sampling overhead changes measured timings. Absence of sampled waits is not proof of no blocking;
  short waits can be missed, and an application-lock resource description can be truncated.
  Output is capped at 5000 DMV rows with an explicit truncation flag and coverage counters.
  The original 9be8d64f run stopped sampling before standalone verification; the follow-up extends coverage through those probes. Do not retrospectively infer waits for the original uncovered intervals.
- Phase timings do not cover clone creation, every guard/fingerprint or complete test-runner overhead.
  The prior 326-second run cannot be explained by subtracting these new phases.
- No production implementation, timeout, lock scope, isolation, schema or service lifecycle changes.
  Keep PR draft until compilation, local measurements and review complete.


## Initial checkpoint-read attribution follow-up

The format-2 ReadCheckpointAsync path performs complete retained verification before returning
its checkpoint/manifest. The slow nine commands are not established to be checkpoint-row SELECTs.
Do not label them writes or blocking until categories and sampled waits establish the cause.

The existing sampler now starts and reports readiness before the initial store read. One outer
try/finally owns sampler cancellation, awaiting completion and diagnostic output for ALL preflight,
publication, readback and verification failures. The runtime task is also observed in its own finally.
The first read uses role initial-checkpoint and phase initial-checkpoint-read; runtime/readback
checkpoint calls retain the separate checkpoint role. next-revision-discovery has its own timer.
Read-only standalone verification is now within sampling coverage as well.

Command categories are fixed table-family hints (contributions, revision inventories, reference-time
transition/cut/outcome, standard authority/version, retained projections/manifests and bindings).
Priority resolves overlapping table names. A category is not a query plan or exclusive execution stage.
Every emitted slow SQL operation includes a SHA256 CommandFingerprint of command text; it does not
inspect parameter values or print SQL text. Fingerprints identify repeated command text, not their
parameter-specific cost. Unknown shapes remain other-sql rather than receiving an invented label.

SqlClient TransactionId is its diagnostic/internal identifier. DMV server_transaction_id and
server_session_transaction_id are SERVER identifiers; do not join those numeric fields directly.
Likewise DMV server_connection_id is not assumed equal to SqlClient ClientConnectionId.
Correlate sources through session ID, session login time, application/role and overlapping monotonic
timestamps. Sampling output keeps those namespaces explicit.

First pull/build and run ONLY the in-memory diagnostic tests (no database is opened):
~~~powershell
git pull --ff-only
dotnet build tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-restore
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ReportingGateCommandTraceTests' --logger 'console;verbosity=normal'
~~~

Historical next-run instructions (HELD pending separate authorization): after review, restore ANOTHER fresh clone from the same accepted
revision-534 backup. In the Stage 3 restore commands, change EVERY target database/data/log name
from FactoryConnect_GG003_GATE_ADVANCE_20261023 to
FactoryConnect_GG003_GATE_ADVANCE_20261023_PREFLIGHT.
Set FACTORYCONNECT_GG003_GATE_ADVANCE_CONNECTION_STRING to that new target as well.
Keep the backup path unchanged. No WITH REPLACE, checkpoint reset, reuse of the revision-537 clone
or Gajra action. Run the advancing filter once and retain detailed output/TRX under a new result name.
A shorter initial read on a fresh run does not explain the earlier 213-second interval; compare
command fingerprints, phase boundaries, wait types, blockers and sampling coverage before attribution.


## R3.1 diagnostic-monitor correction — review/build only

The 7932ff19 PREFLIGHT run advanced 534 -> 537 and reached reporting/readback checks, but the TEST FAILED
when cleanup observed the sampler's earlier SQL timeout. Do not call it diagnostic acceptance PASS.
Initial checkpoint read: 158368.2059 ms; historical-contribution commands: 78 / 151228.9831 ms;
six executions of fingerprint 4BDBBCDE5A0B8C84A52B67617ECA232592BCB90FB576CB5A1B5A3439263D9746
totalled 149808.2512 ms. This fingerprint exactly matches the incorporated-fact query in
SqlContainmentHistoricalContributionReader.ReadAsync. The original run had nine slow commands;
this later run had six. Do not merge counts or equate the two execution histories.
Sampling lasted ~8.8 seconds, completed six cycles and retained five rows; it did not cover the
whole initial read. One PAGEIOLATCH_SH sample with blocker 0 cannot explain the remaining interval.

Correction scope:
- Keep the existing DMV query, nominal 250 ms interval and FIVE-SECOND diagnostic command timeout.
  No retry, timeout increase, query/index change or production lock change.
- Journal connection-open start/completion independently from DMV query-start, ExecuteReader return,
  row-stream completion and whole-cycle completion. Record the interval from previous completion
  to next query start so scheduling/observer delays are visible rather than unexplained gaps.
- Each record carries monotonic timestamps and a snapshot of active named phases (including concurrent
  runtime/publisher/report phases). outside-named-phase explicitly denotes uninstrumented guards/readback.
- On failure, record exception/inner-exception TYPE, SQL error Number when applicable, operation and
  sampler elapsed time, stopped stage/phase, successful sample count, observed rows and last success.
  Do not log exception messages, SQL text or parameter values. Preserve task fault/diagnostic failure.
- Create a unique JSONL journal at artifacts/TestResults/Gg003GateSampler/<application>.jsonl.
  Journal records are auto-flushed; failure/stop records request file flush to disk. Console/TRX prints
  its absolute path. All sampled rows are journaled; console retains its existing 5000-row cap.
  If journal writing fails, retain explicit in-memory error evidence and keep the measurement failed.
- Cleanup prints workloadSucceeded separately from monitorTaskSucceeded/samplerFailed. A sampler
  failure never becomes publication failure. If workload and monitor both fail, retain the workload's
  primary exception and explicitly report the monitor's secondary failure plus its journal.
- Existing command fingerprint/session/role/timestamp correlation is unchanged. SqlClient IDs and
  server DMV transaction/connection IDs remain distinct namespaces.
- File writes and DMV access both add observer overhead. Successful sampling does not prove absence
  of short waits; per-cycle timings locate monitor delay but do not themselves establish its SQL cause.

Build and run ONLY database-free diagnostics for this review:
~~~powershell
git pull --ff-only
dotnet build tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-restore
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ReportingGateCommandTraceTests|FullyQualifiedName~ReportingGateWaitSamplerTests' --logger 'console;verbosity=normal'
~~~

Expected: 13 cases (existing 10 trace cases + 3 sampler lifecycle cases), zero SQL database opens.
Sampler tests inject connection/sample failures and cancellation through the same monitor lifecycle;
they check propagated failures, durable JSONL failure evidence, readiness, and file closure.
New code is not compiled/executed in the editing environment (no .NET SDK). SQL timeout attribution
still awaits a separately authorized measurement. No new clone or rerun is requested at this stage.


## R3.1A critical-phase coverage guard — review/build only

At d91cb0f the developer reported build PASS and all 13 database-free trace/sampler cases PASS.
That does not establish live DMV overhead or sampling completeness. No further SQL run is authorized.

The coverage assessment now binds the exact measured initial-checkpoint-read start/end timestamps:
- Record each COMPLETED query cycle timestamp separately from target-session row observations.
- Only observations from a fully successful cycle count. A partial/failed row stream cannot certify coverage.
- Require at least one successful cycle and at least one initial-checkpoint session observation INSIDE
  the critical interval. Samples entirely before/after the phase are not accepted.
- Sort/deduplicate timestamps, then assess leading, internal and trailing gaps across the interval.
  Record first/last target observation, sample/observation counts, critical duration, maximum cycle gap
  and maximum observation gap. The maximum-gap metrics include their boundary gaps.
- Explicit review policy: maximum gap 1000 ms (four nominal 250 ms sampling intervals). This is
  diagnostic acceptance only, not a production timeout or performance requirement. It is intentionally
  conservative: a fast phase with no in-phase observation is insufficient evidence, not automatic PASS.
- The one-second policy remains subject to this code review; do not loosen it merely to obtain a pass.
- A zero-exception sampler with sparse samples can now FAIL diagnostic acceptance. Workload success,
  monitor-task success, critical-coverage acceptance and journal persistence are reported independently.
- Append the assessment to the closed per-run JSONL journal and console/TRX, including on failure.
  Failure to persist the assessment also rejects diagnostic acceptance.
- Print all diagnostic evidence before enforcing the guard; preserve an already-propagating workload
  or sampler error rather than masking it with a second coverage assertion.

The DMV SQL text, five-second timeout, nominal sampling interval, command fingerprint/correlation and
publication behavior remain unchanged. Existing separate connection/query/row-stream cycle timings
will measure overhead when a SQL run is separately authorized; this change does not fix or establish
the cause of the earlier monitor timeout.

Nine new database-free coverage cases cover dense sampling, absent/outside samples, missing target
session evidence, leading/middle/trailing gaps, duplicate/unordered timestamps and a missing phase.
The existing lifecycle failure test also checks durable rejected-coverage persistence without clearing
the original fault.

Build/validate ONLY:
~~~powershell
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw 'Pull failed.' }
dotnet build tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed; tests stopped.' }
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ReportingGateCommandTraceTests|FullyQualifiedName~ReportingGateWaitSamplerTests|FullyQualifiedName~ReportingGateCriticalCoverageTests' --logger 'console;verbosity=normal'
~~~

Expected 22 cases; no SQL database opens. No .NET SDK is available in the editing environment,
so local compilation/execution remain pending. Preserve both revision-537 clones. Review the
implementation and these results before deciding on a new disposable-clone measurement.


## R3.1B separate read-only DMV overhead probe — code review only

At c4473bf the R31A clone run advanced 534 -> 537 with readback checks, reproduced the reporting
shared-gate timeout naturally (~15.00 s), and FAILED diagnostic acceptance. Five historical-contribution
query executions totalled ~126.95 s inside exclusive publication ownership (gate-to-commit-return
upper bound ~130.84 s; commit ~3.17 ms). Historical SQL slowness remains unexplained.
The monitor connection opened in ~794 ms; its fifth DMV query failed before ExecuteReaderAsync
returned, after ~5.16 s, SQL error -2. Critical trailing observation gap ~160.78 s correctly failed
the accepted one-second coverage policy. Preserve the R31A and earlier revision-537 clones.

New source: ReportingGateDmvOverheadProbe.cs; database-free tests:
ReportingGateDmvOverheadProbeTests.cs. This is a separate workstream from historical-query tuning.

Read-only design:
- Only fixed system-DMV SELECT queries, plus a DB_NAME/IsLocalDB guard. No FactoryConnect stores,
  processor gate calls, projection cycles, reconstruction, checkpoint reads/writes, migrations or DML.
- Requires an independently prepared LocalDB restore named FactoryConnect_GG003_DMV_RCA_<suffix>.
  The preserved GATE_ADVANCE/GATE_RCA clones are rejected. Do not create or run anything yet.
- Connection strings use ApplicationIntent=ReadOnly, pooling disabled and diagnostic ConnectTimeout=5.
  ReadOnly intent is connection intent; the fixed SELECT-only measurement code defines this probe's
  read-only behavior. Each variant has a separate non-pooled connection.
- One passive anchor connection with no transaction/gate makes the application filter nonempty.
  Only that anchor and the active observer intentionally match the random probe application prefix.
  This does not simulate the activity/lock population of an advancing publication.
- Variants execute sequentially, in fixed order: sessions; sessions+requests;
  sessions+requests+connections; full-existing (transactions and APPLICATION-lock joins).
  The full-existing query preserves the sampler SQL text; a normalized-line-ending SHA256 test binds it.
  Do not optimize the full query or remove ORDER BY/joins as part of this comparison.
- Default three cycles per variant (allowed 1–10), nominal 250 ms inter-cycle delay, five-minute
  overall cancellation budget. Every cycle creates a NEW command with CommandTimeout=5 and its own
  @Prefix parameter. That SqlClient command timeout is not a strict five-second whole-cycle wall clock.
- Separate stage records: connection open, ExecuteReaderAsync, ReadAsync consumption. Full-cycle
  elapsed, total rows, distinct session count and rows/session expose result multiplication.
  Start/completion timestamps and completion-to-next-start intervals expose scheduling/disposal gaps.
  Metadata only: no SQL text, parameter values, connection strings, exception messages or machine payloads.
- Variant, cycle, stage, exception TYPE, SQL Number and elapsed time are journaled before propagation.
  Failure terminates the probe and is never recorded as a successful sample. Earlier completed cycles
  remain evidence. There is no timeout retry or replacement of a failed measurement.
- Unique JSONL journal under AppContext.BaseDirectory/artifacts/TestResults/DmvOverhead; the exact
  absolute path is printed. Auto-flush per record and disk flush on failure/finalization preserve
  evidence. Journal failures are explicit; probe failure is not swallowed.
- Row streaming and journal I/O can contribute observer overhead; success while idle does not establish
  sampling reliability during a 130-second exclusive publication. Variant order, caching, SQL permissions
  and other LocalDB instance activity can affect comparisons. Do not attribute the prior timeout from
  one successful baseline run.

Separate estimated-plan inspection:
- Disabled by default; optional only under explicit SQL execution authorization and separate plan opt-in.
- Uses a new non-pooled connection per variant and session-only SET SHOWPLAN_XML ON, then obtains an
  ESTIMATED plan without executing the SELECT. Closing the connection discards that session setting.
- Five-second timeout per plan command; stage failures propagate and are journaled.
- Save sanitized .sqlplan files beside the journal; remove compiled/runtime parameter-value attributes
  and replace the generated application prefix before writing. No actual-plan execution/capture.
- Estimated plans help inspect join order/cardinality assumptions, but do not establish actual wait cause.

Opt-in environment variable names for review (DO NOT set/run until separately authorized):
- FACTORYCONNECT_GG003_DMV_PROBE_CONNECTION_STRING
- FACTORYCONNECT_GG003_DMV_PROBE_AUTHORIZATION (explicit R3.1B-READ-ONLY marker)
- FACTORYCONNECT_GG003_DMV_PROBE_CYCLES (optional, default 3)
- FACTORYCONNECT_GG003_DMV_PROBE_ESTIMATED_PLANS (optional 1, default off)
Missing Windows/connection/authorization skips the SQL fact; a wrong marker/unsafe target fails
before opening SQL. A skip is not acceptance.

Current authorized actions: pull, build and DATABASE-FREE tests only:
~~~powershell
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw 'Pull failed.' }
dotnet build tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed; tests stopped.' }
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ReportingGateDmvOverheadProbeTests' --logger 'console;verbosity=normal'
~~~

Expected 18 database-free cases. The editing environment has no .NET SDK or developer SQL instance;
compilation/execution remain pending. Keep PR draft. Code/build review must precede a separate
authorization decision for SQL measurement. No clone creation, SQL execution or Gajra action is requested.
