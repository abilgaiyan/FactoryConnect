# B1 concurrency evidence scope

Accepted schema checkpoint: `0e20aee379cc99396df3b67e1737d94cefff6b30`, CI run `39049828060`: both jobs PASS. This includes four isolated schema tests, populated legacy V1 upgrade/replay and the dedicated GG-003 historical-reader test. Original batch failure remains in `95457396` and run `39047365520`.

## Supporting source-writer measurements

`B1SourceWriterLockCharacterizationTests` invokes the actual aggregation writer (including R0/cut publication), reference-time transition Begin, transition Complete, claim-bound outcome admission and legacy outcome writer. Each case owns a newly created isolated database. No production store implementation is modified.

A separate transaction holds a table X lock at the selected source boundary. The actual writer starts; the observer must find that writer waiting on the blocker in `sys.dm_exec_requests`, then captures its request SQL, wait resource and held/waited locks from `sys.dm_tran_locks`. Polling time alone never proves the barrier. Transition paths must also show granted locks on the transition table before waiting at contributions. The blocker rolls back, the actual writer must finish, and checkpoint/outcome/transition assertions run. No deadlock retry is present; SQL errors fail the test and are retained in TRX output.

These broad fixture locks deliberately expose source acquisition points. They are **not the candidate historical validator's lock footprint**, do not establish row/range-lock compatibility, do not capture a complete acquisition sequence and do not constitute C1–C8 acceptance. Captured waits are point-in-time observations; absence of a deadlock in these cases is not proof of deadlock freedom. No deadlock graph is claimed unless an actual deadlock is captured separately.

Evidence is retained as the `b1-source-lock-evidence` CI artifact, with synthetic database identity, SQL product version, path, blocker/writer SPIDs, blocked request SQL, lock objects/modes/status/resources and post-release outcomes. Artifacts have ordinary GitHub retention limits; retain a reviewed copy before expiry.

## Remaining acceptance prerequisites

The current `SqlServerMetricAggregationStore.ReadAtRevisionAsync` creates its own connection and its reference-time helpers open additional connections. It accepts no publication connection/transaction. It therefore cannot be used unchanged to prove RC2's transaction-aware historical authority gate.

The existing publication transaction exposes a callback after gate/processor/checkpoint acquisition; `PublicationLocks.PrepareAsync` establishes the retained projection/manifest/evidence prefix. Neither currently executes the proposed V2 archive writes or a historical authority validator. Source transition stores own their transactions and expose no deterministic callback after target-lock acquisition. Fixture SQL barriers may characterize their behavior without changing production code, but do not fill the V2 validator gap.

Before C1–C8 acceptance, a validation-only candidate must concretely define and execute source query order, exact A/R/standard/source membership checks on the caller's transaction, archive-key ordering, full replay comparison and failure stages. It must then be interleaved with the actual writers/readers, using measured barriers and retained locks/deadlock events. No production V2 implementation or activation is authorized by these supporting probes.

| Gate | Disposition at source-characterization addition |
| --- | --- |
| Source writer observations | CI execution required; report separately |
| C1 publication/aggregation | NOT EXECUTED |
| C2 publication/transition completion | NOT EXECUTED; source-path observations alone cannot close it |
| C3 publication/outcome admission | NOT EXECUTED; source-path observations alone cannot close it |
| C4 replay/publication | NOT EXECUTED for V2 |
| C5 reporting/publication | NOT EXECUTED for V2 |
| C6 shared witness publications | NOT EXECUTED |
| C7 source-local cycles | NOT EXECUTED as the complete multi-writer schedule |
| C8 failure/cancellation | NOT EXECUTED for all V2 transaction stages |
| B1 freeze / B2 | HELD |
