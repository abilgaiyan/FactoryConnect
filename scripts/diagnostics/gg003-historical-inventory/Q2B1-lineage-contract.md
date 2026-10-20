# Q2B.1 — Revision 534 lineage contract and SQL review candidate

Baseline: 65e78b6b82c5930b83f1357e641948c0e836d331. New candidate; not an execution-approved script. No database connection, syntax/plan validation, query or replay performed.
File: Q2B-revision-534-lineage-review.sql. It does not replace or certify any separately frozen PR #140 snapshot.

## Repository contract, not column-name inference

MetricAggregationCheckpoint.Position has type MetricInputPosition. MetricAggregationProcessingRuntime.RunCycleAsync sets the proposed checkpoint from batch.ThroughPosition.
SqlServerMetricAggregationStore.StageNewInputsAsync requires each input not exceed the proposed checkpoint, resolves the exact durable fact, rejects duplicate positions/identities and rejects newly introduced positions <= the expected checkpoint.
The same commit inserts contribution rows with the original input.Position, inserts the revision at proposed checkpoint.Position and advances the checkpoint transactionally.
SqlContainmentHistoricalContributionReader verifies exact processor/stream and committed revision, then selects the processor's contribution rows with c.Position<=cut and LEFT JOINs by fact row ID, stream row ID AND position.

Therefore aggregation revision position is a committed watermark in the bound metric-input position domain in this implementation, not an independent ordinal. Raw observation positions remain a different domain.
A standalone MetricInputFact WHERE Position<=534 is unsafe because existence is not admission; the numeric bound on the exact processor contribution ledger is the implemented historical membership contract.
Do not infer gap-free membership or late-backfill safety outside the supported writer protocol.

Source paths (all at baseline):
- src/FactoryConnect.Abstractions/MetricAggregationCheckpoint.cs
- src/FactoryConnect.Core/MetricAggregationProcessingRuntime.cs
- src/FactoryConnect.Persistence.SqlServer/SqlServerMetricAggregationStore.cs (StageNewInputsAsync, InsertContributionAsync, commit)
- src/FactoryConnect.Persistence.SqlServer/SqlServerMetricAggregationRevisionPublication.cs
- src/FactoryConnect.Persistence.SqlServer/SqlContainmentHistoricalContributionReader.cs

## Preserved Q2A evidence

Uploaded first-page output identifies machine 677b030b-1ede-4a67-82ae-6a494791357f, metric-input stream row 1, aggregation processor row 1 and committed revision 534. Revision inventory reports 746 revisions, from 10 through 2150; terminal output shows an empty page after (1,2150) and unchanged READ_ONLY target.
These reads ground candidate selection; this work does not re-audit all 746 page rows or claim historical authority completeness.
The terminal fact overview contains 16-18 October data and no 19 October rows. This is scope evidence for this copy, not proof that 19 October evidence never existed elsewhere.

PR #140 remains draft at 1bf700365ae77809e53b51a3a7b72f2f2c2f7959 during inspection. It uses filename Q2B-exact-cut-fact-lineage.sql, not Q2B-exact-cut-lineage.sql. Absence from a separately frozen snapshot does not imply execution approval; this is a separately reviewed candidate from current main.

## Seven result sets and limits

| Set | Expected bound / meaning |
| --- | --- |
| Q2B-target | 1 row, exact disposable server/database, reader and read-only state |
| Q2B-cut | 1 row, processor 1 + stream 1 + exact committed watermark 534 |
| Q2B-contribution-boundary | 1 row; admitted count/extrema vs stored-but-not-exact-members diagnostics |
| Q2B-fact-membership | All ledger members <=534, no date/key filter, no TOP truncation |
| Q2B-duration-operands | Group counts by full stored shift/day identity, key, unit, eligibility; no floating sums |
| Q2B-coverage | 1 row; missing joins, stream/binding/machine failures, null references, duplicate position/fact groups |
| Q2B-end-target | 1 row; unchanged read-only target |

Membership cap 1000 aborts before result output. Under valid unique positive integer positions at 534 there can be at most 534 members; this is an upper bound, not an asserted actual count. The seven-set full extraction avoids a partial-page claim for this narrow cut.
Coverage is referential diagnostics, not proof of activity completeness. Existing wrong-machine and absent-fact/unverifiable counts are separate; categories overlap and must not be added. Duration groups omit failed joins but coverage preserves their counts; any failure blocks arithmetic interpretation.
Null references may be legitimate for other fact families; classify by key before treating them as errors.
Payload lookup/identity validation, duplicate logical identity analysis and historical schedule authority are subsequent gates; scalar SQL references are not resolved here.

## Historical operand reconstruction after accepted extraction

Reconcile fact-membership row count and unique contribution positions to boundary/coverage totals. Reject unresolved exact joins, wrong binding/machine or duplicates before interpretation.
For each exact production-day identity and separately each full shift occurrence, select admitted duration.running (all eligibility values) for legacy ActualProductionTime; duration.planned-production for PlannedOperatingTime. Scheduled/stopped inputs remain separate; never add them into running.
Retain original MetricValue strings. Use independently reviewed .NET-decimal-compatible exact parsing/summation with overflow checks offline, in contribution order, matching legacy aggregate semantics. Do not cast to a chosen SQL scale that could round stored values.
Compare reconstructed sums/counts and source identities against historical operand evidence only if that evidence is exact-cut bound. Current aggregates/current projections cannot substitute for revision 534.
Known RCA diagnostic expectations (not query results): running 2683.614157 seconds, planned 2452.013355 seconds, ratio about 1.09445332. Failure to reproduce must be investigated; never filter facts until the target number appears.
No replacement Availability, V2 result publication or reporting replay is authorized by this analysis. Successful operand reproduction still does not establish factory-approved schedule or complete planned activity.

## Review and execution gates

Review exact SQL against uploaded Q1 columns/keys; compare complete packet hash and local parameterized hash; estimated-plan/syntax validation requires separate non-production authorization. No SQL Server tests performed here.
Candidate resource envelope: 60-second client timeout, 10 MiB untruncated output, one full <=1000-member batch, restricted reader, immutable approved restore. Confirm plans/indexes before approval; row bound does not guarantee physical I/O. Use sqlcmd -b in a separately reviewed command and capture exit code plus every set without text/column truncation.
No command is issued now. Execution requires explicit exact commit, file hash, target/operator, timeout/output path and limits authorization. On error/truncation/missing final target set, inventory is incomplete.
Q0/Q1 and setup are not repeated. Runtime implementation, schema changes, data repair, replay and Gajra production access remain held.
