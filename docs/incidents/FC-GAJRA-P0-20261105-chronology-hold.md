# FC-GAJRA-P0-20261105 — chronology containment

Draft. Restored-production-SQL chronology correctness accepted; full-host liveness and release qualification remain pending. Merge and production deployment are not authorized.
Deployed baseline: 85f56f32880dd1b47be1a605c85da2a86d4fc304.
PR base: 088bf52492546a2a79029cf366a22f7a4d7e8d44.

## Exact boundary

GG-003, stream mtconnect:DEWAS-SR-GG-003, processor machine-state-activity:

- Authority position 513501, revision 4091; active state Unknown.
- Active start 2026-11-05T08:35:08.9193810Z; last consumed instance 1793867708.
- Execution position 513521, same instance, sequence 103, STOPPED/Good at 2026-11-05T08:26:00.4417150Z.
- Policy continuity/preserve 1.0. Earlier quality 1 is named Uncertain in code.

The actual normalizer maps STOPPED to false; the evaluator transitions Unknown to Stopped. The deployed calculator proposes a negative Unknown activity period. A frozen, test-only copy of that calculator provides the reproduction oracle. The user reports the exact restored-production-SQL acceptance passed on November 5, 2026: 1 passed, 0 failed, 0 skipped; test execution 25.27 seconds at runtime commit 423e83b9c7aea26f5589a5a2b6b4d3065ec94191. The restored test reproduced this boundary, verified no writer calls and unchanged authority/signals/history/checkpoints, and reconstructed the same hold twice. This developer-machine result was not independently rerun in the assistant workspace. It establishes chronology correctness, not complete-host restart or MOXA liveness.

## Containment and checkpoint ownership

A state transition that would close an active period before its start returns a typed chronology hold. The complete calculation batch is rejected, including earlier tentative outputs. The processor does not call PublishAsync. Runtime/pipeline handle the outcome explicitly; no broad exception handler is introduced. Legacy void/result-less entry points throw on a hold so callers cannot mistake it for accepted processing.

The held activity cursor is MachineStateActivityAuthority.Position (513501 here). ProjectionRevision, evaluation authority, signals and histories stay unchanged. There is no separate activity checkpoint update in this runtime. Canonical mapping owns the separate ObservationProcessingCheckpoint entry canonical-mapping; that checkpoint can advance. Acquisition, quantity and metric processors receive no hold. This does not guarantee downstream metrics can advance without new activity input.

The pipeline continues to MOXA after the typed hold. Other exceptions retain their failure behavior. A hold does not count as progress for polling delay. Edge logs a new/changed hold and logs it again after process restart; identical polls do not flood logs.

Diagnostics retain mapped value/type/quality, position, instance, sequence, timestamp, source/address/signal, active start/state, proposed state, committed position/revision and policy. Original raw STOPPED remains in MachineObservation; its mapped value is false. No timestamp or raw value is rewritten.

## Restart reconstruction

The canonical sink is in memory while mapping progress is durable. Restart could otherwise omit the conflict after mapping advanced. Edge activity reads reconstruct mapped input from retained raw evidence after the activity cursor, bounded by the canonical-mapping checkpoint pinned for that read. The same configured normalizer and mapper run against a private in-memory coverage store. Reconstruction writes no SQL state or checkpoint.

Returned pages contain at most the configured batch size; unmapped pages are scanned until mapped input or the frontier. This adds raw reads/remapping and assumes the same commissioned configuration. Repeated sparse-suffix scans and latency require acceptance observation. This change does not establish physical source completeness or authorize configuration changes. No schema, SQL writer validation, isolation, break, OEE calculation or deployment changes are included.

## Verification commands

Keep Edge/acquisition stopped against the restored database. From the PR checkout:

```powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter "FullyQualifiedName~GajraChronologyHoldTests"

$env:FACTORYCONNECT_P0_CHRONOLOGY_CONNECTION = "Server=DESKTOP-1PJK2OB\FCINVENTORY;Database=FactoryConnect_P0_20261105;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"

dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter "FullyQualifiedName~GajraRestoredChronologyAcceptanceTests" --logger "console;verbosity=detailed"
```

The opt-in restored test permits only the exact server/database. It reads actual authority/raw observations, checks captured identity and timestamps, reconstructs mapped input, captures the deployed negative proposal and executes the corrected activity runtime twice with fresh reconstruction state. SQL operations are SELECT-only and an authority proxy rejects every writer call. Complete authority/signals/state-history/activity-history/processing-checkpoint captures must match before/after. No Edge host, migration, recovery, DML or checkpoint intervention runs.

Synthetic tests separately prove whole-batch rejection, equality timestamp behavior, distinct instance reset/preserve semantics, unchanged activity cursor, advancing mapping checkpoint, actual MOXA status normalization/independent advancement, restart reconstruction, notification behavior and propagation of unrelated failures. Live MOXA SQL advancement remains a separate gate; the read-only test does not claim it.

## Release gates

Focused and relevant existing regression tests passed, and the user reports exact restored-SQL chronology acceptance PASS. Full-host liveness, replay cost and release qualification remain open under Gates A–D below. GG-003 evidence resolution/resumption remain unapproved. No skipping, timestamp clamping, reordering, history rewrite or checkpoint repair is supplied. OEE Help Page inclusion belongs to the later exact-commit release; this PR does not build that release or activate breaks.

## Workspace validation

- Focused and related integration regression selection: 35 passed; restored-SQL acceptance skipped because its opt-in connection is unavailable in this workspace.
- Edge test project: 242 passed. The Linux workspace needed its missing Asia/Calcutta timezone alias supplied outside the repository; no application timezone logic was changed.
- Core and affected projects compile with analyzers enabled.
- Subsequent developer-restored-SQL chronology acceptance: user-reported PASS, 1 passed / 0 failed / 0 skipped, 25.27 seconds. The earlier workspace skip remains a historical execution result, not the current gate disposition.

## Final qualification gates

Acceptance standard: GG-003 remains safely held, MOXA-001 makes durable progress within a measured and accepted interval, and this behavior survives a complete Edge-host restart.

| Gate | Required evidence | Disposition |
| --- | --- | --- |
| A — Replay performance | First cycle separately; at least 10 consecutive steady-state cycles; retained-reader time distinct from calculator time; raw pages/rows, SQL duration, per-stream and whole pipeline-cycle durations; median and maximum | Pending |
| B — Full-host isolation | Actual MOXA SQL activity position and revision advance while GG-003 stays at position 513501/revision 4091; measured MOXA progress interval | Pending |
| C — Host restart | Controlled full Edge-host stop/start; same GG hold reconstructed; actual MOXA SQL activity advances again | Pending |
| D — Release compatibility | Clean candidate package startup, existing configuration compatibility, controlled shutdown, and rollback rehearsal against the pinned production package in an isolated environment | Pending |

### Measurement record

For Gate A retain one row per cycle and stream, including cycle ordinal, first/steady-state classification, UTC start/end, stream identity, pinned mapping frontier, activity position/revision before and after, raw page/row counts, SQL command count and elapsed time, retained-reader elapsed time, calculator elapsed time, stream elapsed time, and full pipeline-cycle elapsed time. Retain all individual samples before computing median/maximum. Distinguish SQL timings from reader wall time, which includes mapping and materialization. Do not treat the 25.27-second acceptance-test duration as a replay benchmark: it includes whole-table snapshots and multiple reads.

The scheduler is sequential. Each retained read creates a fresh mapper and may traverse multiple unmapped raw pages before returning. A typed hold is not proof of low latency. Measurement precedes caching, parallelism or scheduler changes. Set and record the acceptable MOXA durable-progress interval before declaring Gates A/B passed; no numerical liveness threshold has yet been approved. If blocking is unacceptable, propose a separately reviewed, narrow scheduling correction rather than expanding this containment patch into a concurrency refactor.

### Isolation and evidence

Preserve FactoryConnect_P0_20261105 and the incident backup. Gates B/C require a separate disposable restore, with factory acquisition disconnected. Any injected test observations must be explicitly labeled synthetic in the acceptance record, limited to that disposable database, and traceable by exact stream/instance/sequence/position. Never inject into the preserved restore. A pipeline log line is not durable progress: capture actual SQL activity position and revision before/after and the elapsed progress interval. No qualifying MOXA transitions in the source means the test is inconclusive, not PASS.

For Gate C construct a new host process with fresh in-memory state, not merely a new calculator or runtime object. Verify controlled termination and re-read SQL after restart. Do not change GG-003 checkpoints, retained observations or authority to make the test advance.

For Gate D rehearse the candidate package and known pinned production package 85f56f32880dd1b47be1a605c85da2a86d4fc304 only in the local isolated environment. Record package/source identity, configuration identity, startup/shutdown evidence and rollback outcome. The pinned release still contains the known chronology failure; a successful package rollback does not establish healthy GG-003 processing. Do not apply either package to the factory machine during this review.

Current disposition: restored-SQL chronology correctness accepted. Gates A–D remain pending. PR #180 stays Draft; merge, production deployment and GG-003 evidence resolution remain unapproved.

## Gate A measurement design — no runtime correction authorized

This is a design for measurement, not a completed benchmark. Runtime behavior stays at 423e83b; the accepted restored-SQL chronology PASS is unchanged. No caching, concurrency, scheduler, persistence or evidence-resolution change is part of this design.

### A1 — Read-only retained-reader and calculator measurements

Use the exact named incident restore with all writers stopped and retain its identity. Record runtime commit, documentation commit, process start, configured batch size 100, polling interval 1 second, SQL instance/database identity, authority position 513501/revision 4091, mapping checkpoint/frontier and configuration evidence identity. Use the same commissioned replay configuration; never infer equivalence solely from the STOPPED-to-false output.

Construct the SQL raw reader, checkpoint reader and retained-input reader once, then perform one first-process sample followed by at least 10 consecutive steady-state samples in the same process. Do not add an unreported warm-up or flush server caches. The reader must retain its actual per-read mapper construction; do not introduce a measurement cache. Use monotonic elapsed timing and UTC correlation timestamps.

For every sample:

1. Time the complete retained-reader call from request to returned mapped batch. Count every underlying raw page, returned raw row, eligible row at/below the frontier and returned mapped row. Record the pinned canonical-mapping frontier.
2. Separately time the production calculator over that returned batch and unchanged persisted authority with continuity/preserve 1.0. Require the exact hold at position 513521 and matching timestamps; stop/classify any mismatch rather than continuing a misleading benchmark.
3. Capture SQL command elapsed time and command count through measurement instrumentation correlated with that sample. Include checkpoint and raw-page commands, and distinguish connection-open, command execution/materialization and mapper time where observable. A wrapper around the whole reader is not a SQL-only timer. If SQL-only timing is unavailable, mark it unmeasured; never report reader time as SQL duration.
4. Keep before/after protected-state comparisons outside the timed sample interval. They remain required for the measurement run's integrity, but do not belong in replay latency.
5. Persist raw samples before calculating summaries. Record failures, cancellations and incomplete instrumentation as such; they are not zero-duration samples or successful holds.

A1 performs no PublishAsync and no checkpoint commit. It does not need to rerun the full accepted correctness test or serialize whole SQL tables inside each iteration. A write-rejecting proxy and, where available, a SELECT-only database identity provide additional execution protection. Diagnostic instrumentation must not change mapping, reads, transactions or source evidence.

### A2 — Sequential pipeline-cycle measurement

Use a separate disposable incident restore for any composed pipeline/host run that can advance canonical mapping or MOXA authority. Preserve the original restore. Disconnect factory acquisition. Use the actual stream order, batch size, polling configuration and production service composition.

Measure raw mapping stage, retained-reader stage, calculator/hold stage and total stream duration separately for GG-003 and MOXA. Measure the full pipeline-set iteration, including notification overhead, and record polling delay separately from processing time. Obtain actual SQL MOXA position/revision and the elapsed interval between durable advancements. Do not sum overlapping inclusive timers as if they were exclusive costs.

Capture the first iteration separately, then at least 10 consecutive steady-state iterations with qualifying MOXA input. A1 cannot stand in for A2: a cheap calculator does not establish a cheap retained read, and a cheap retained read does not establish full-host liveness. A run with no eligible MOXA transitions is inconclusive for advancement.

Retain a bounded run/watchdog setting and cancellation outcome in the execution record. No accepted liveness threshold is currently frozen; report the measurements and obtain an explicit interval decision before labeling Gate A/B PASS. Measurement instrumentation is still to be implemented/reviewed; no measurements are claimed by this document.

### Output schema and summaries

One sample row should contain: run ID, process ID, cycle/sample ordinal, first/steady classification, stream, UTC start/end, pinned mapping frontier, authority position/revision before/after, raw pages, raw rows, eligible rows, mapped rows, SQL commands, SQL elapsed milliseconds, connection-open milliseconds if observed, raw-mapping milliseconds, retained-reader milliseconds, calculator milliseconds, stream milliseconds, pipeline-cycle milliseconds, polling-delay milliseconds, outcome and instrumentation gaps. Unavailable fields are null with a reason.

Report first-process results individually. For the 10 or more steady samples report count, median and maximum for each duration, separately by stream, plus whole-cycle duration and MOXA durable-progress interval. For even sample counts compute the median as the mean of the two central sorted values. Keep command/page/row counts per sample so variation can be explained. The 25.27-second correctness-test execution remains excluded as a replay-performance observation.

## Commissioned replay configuration equivalence — evidence review

Evidence reviewed: the supplied November 5 configuration capture, Pasted text(20261105-091600).txt, ObservationProcessing section; acceptance helper GajraChronologyHoldTests.Mapper; and production EdgeObservationProcessingServiceCollectionExtensions at runtime commit 423e83b. This is captured-settings comparison, not independent observation of the effective running host.

| Setting | Captured commissioned GG-003 setting | Acceptance/replay code | Disposition |
| --- | --- | --- | --- |
| Machine | 677b030b-1ede-4a67-82ae-6a494791357f | Same GG fixture identity | Match |
| Stream | mtconnect:DEWAS-SR-GG-003 | Same GG fixture identity | Match |
| Normalizer | ExecutionNormalization | ExecutionNormalizationProcessor | Match |
| Normalizer source/address | mtconnect / DEWAS-SR-GG-003_p1_ctl_exec | Same values | Match |
| Mapping source/address | mtconnect / DEWAS-SR-GG-003_p1_ctl_exec | Same values | Match |
| Canonical signal/type | state.running / Digital | CanonicalSignalKeys.Running / Digital | Match |
| Invert | false | Omitted Boolean property defaults to false | Match |
| Mapping entries for GG | One | One | Match |
| Batch/polling | 100 / 1 second | Restored runtime options 100 / 1 second | Match |
| Applied continuity | Restored authority continuity/preserve 1.0 | Checked against canonical Preserve policy in executed acceptance | Match within accepted test |

Production live mapping and retained replay use the same CreateMappingProcessor factory and per-stream mappings/normalizer selections. Retained replay substitutes a private sink and in-memory coverage store; it does not select a different normalizer implementation. Source review establishes this code-path equivalence.

Outstanding configuration evidence: capture the local qualification host's effective ObservationProcessing stream entry, normalizer selection, mapping list, batch/poll settings, continuity policy and DemoCanonicalInputs flag after all configuration providers are applied. Demonstrate that no command-line/environment override changes those values and that demo normalization is disabled. Record a deterministic, redacted projection/hash of those relevant settings and the commissioned capture identity; do not hash or publish credentials. Verify uniqueness and absence of extra state-driving mappings. A raw file hash alone does not prove effective configuration, and identical output for one observation does not prove configuration equivalence.

Configuration disposition: captured GG settings match the acceptance helper; production mapping/replay share the same factory. Effective-host provenance verification remains pending. This qualification does not invalidate the completed restored-SQL correctness acceptance.
