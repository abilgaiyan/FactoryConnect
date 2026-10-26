# D1-A1 protected historical-read feasibility: frozen experiment contract

Status: frozen design baseline. Diagnostic implementation is authorized and a test-only
harness is now provided in Gg003ProtectedReadFeasibilityTests.cs. The original SQL run produced 21/23 PASS with boundary-deadlock and timeout failures.
Revised setup compilation and SQL reruns remain pending; D1-A1 is not accepted. See gg003-d1a1-execution.md for exact coverage and gaps.
D1-A2 production optimization remains blocked.

## Git and evidence boundary

Start at PR #159 commit 29b726012c24033248ed006061677b2e6afd21cb.
The D1-A1 draft initially targets test/gg003-acquisition-a2-clone-benchmark.
Keep PR #159, its successful A2 benchmark, D1-C tests and reported 8/8 results
unchanged. After PR #159 is accepted and merged, review the stacked diff and
retarget this PR to main; squash ancestry may require reconciliation first.

Preserve the advanced A2 clone at ordinal 40,546 and its TRX. The real-history
reference remains an 81.31-second append transaction and 95,274 predecessor source
commands. No synthetic feasibility result replaces that reference.

## Experimental boundary

Future implementation is confined to diagnostic test and documentation files.
Use a separately named, separately acknowledged disposable LocalDB clone and new
synthetic machine/stream fixtures. No mutation targets restored GG-003 history.
No production ingestion changes, schema changes, migrations, lock hints, forced
indexes or deployment are included.

An explicit Serializable transaction is permitted only as the proposed test
candidate. Production transaction
settings remain unchanged.

## Candidate lookup requirements

Execute the intended bounded lookup query shapes, not a substitute SELECT chosen
to acquire convenient locks. Source predicates bind exact machine, binary stream,
instance and sequence pairs. Boundary predicates bind exact machine, binary stream
and position. Initial bounds are 128 source identities, 128 boundary positions and
128 publication windows, with continuation for oversized publications.

Capture actual execution plans, accessed indexes and observed lock resources.
Protect source content, identities, positions and relevant existence/absence
predicates through the final dependent validation. Returned-row protection alone
does not establish gap protection. Treat wider locks as contention costs, not as
evidence of a preferred plan.

## Deterministic schedule points

1. Before the candidate protected lookup.
2. After its reader is fully consumed and disposed.
3. Between publications referencing the same observation.
4. After final dependent validation, before transaction completion.
5. After commit or rollback.

Coordinate using barriers and observed SQL waits with bounded failure deadlines,
not arbitrary sleeps. Document any point that cannot pause the actual verifier
without production instrumentation; do not replace it with a simulated outcome.

## Protection schedules

| Concurrent operation | Required evidence |
| --- | --- |
| Content update or existing source/boundary deletion after read | Cannot invalidate a later dependent validation while protection is required |
| Instance, sequence or position change | Protected identity and boundary relationships remain valid |
| Insert conflicting existing identity/position | Existing constraint rejects conflict, with actual blocking/order recorded |
| Insert identity or boundary previously observed absent | Protected absence remains stable through the required interval |
| Mutation committed before protection starts | Actual verifier sees resulting state and rejects invalid evidence |
| Cancellation, rollback or verification failure | No partial publication; locks release and contender outcome is recorded |

Absent-row cases are predicate probes, not fabricated valid publications with
missing evidence.

## Separate equivalence gate

Blocking tests alone do not establish verifier equivalence. Baseline and candidate
must execute the actual verifier on equivalent evidence under matched schedules.
No tested baseline rejection may become candidate acceptance. Compare corruption,
unsupported encoding, SQL errors and cancellation classifications. Preserve atomic
checkpoint, observation, contact-authority, evidence-publication and head behavior.

A prototype that only runs the old verifier under stronger isolation must be
described accurately; it is not a completed bounded-reuse implementation.

## Measurements

Record session/transaction IDs, effective isolation and database options, actual
predicates/plans/indexes, key/range lock modes/resources, waits, commit order,
evidence results and persisted state. Deadlocks and timeouts are failures requiring
review, not successful protection.

Compare matched synthetic baseline/candidate SQL counts, append/replay wall times,
transaction lifetime, writer latency and reporting-reader latency. State whether
historical-lock holding times are measured directly or only bounded. Performance
and contention are separate conclusions. Full-history comparison requires another
fresh restore and equivalent workload.

## Exit and authorization gates

- Predicate protection, including absent identities and boundaries.
- Actual plan/lock/wait evidence.
- Actual verifier equivalence for tested schedules.
- Cancellation, rollback and failure atomicity.
- Synthetic contention and performance measurements.
- Explicit coverage gaps and an enforceable consistency argument.

Implementation alone supplies no SQL execution results.
Do not close D1-A1 merely because individual writers block. D1-A2 requires a
separate production design review and authorization after these results.


## Failure investigation boundary

The original partial-read-set boundary schedule remains an unresolved failed
experiment. Its separately opted-in reproducer retains the no-deadlock assertion.
The revised standard fixture protects its complete source dependency set before
starting a mutator; success there must not be presented as proof for partial windows.
Session lock-timeout setup now uses parameter-free commands and verifies the effective
value; expected SQL error 1222 is unchanged. See the execution runbook for original
evidence, hypotheses, lock/graph capture and individual rerun commands.
