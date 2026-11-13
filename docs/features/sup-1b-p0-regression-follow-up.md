# SUP-1B P0 regression follow-up

PR #172 remains draft. Merge and production activation remain on HOLD.

This regression-only packet adds three SQL cases, without changing the security implementation, schema, routes, or configuration.

- `HashUpgradeRejectsOldSnapshotButPreservesExistingSessions`: installs a valid low-iteration Identity hash in an isolated fixture and first confirms `SuccessRehashNeeded`. A deterministic barrier holds one login after its credential snapshot. Another login upgrades the hash and advances authentication generation exactly once. The stale attempt receives no session; the earlier session remains usable, the security stamp remains unchanged, only two sessions are current, and the administration revision remains unchanged.
- `ResetCommitFailureReconcilesUnderTargetSubject(false)`: cancels after reset writes but before commit. Persisted security state remains byte-equivalent when serialized; the old session remains valid, and no reset completion/result is visible. Retrying the same operation and request succeeds once.
- `ResetCommitFailureReconcilesUnderTargetSubject(true)`: injects acknowledgement loss after actual reset commit. Retrying the same operation returns the historical result. Both reset cases assert one generation increment, old-session revocation, one completed operation under the target subject, one reset completion audit, identical replay without state mutation, and historical retrieval only through a current session with the independent retrieval grant. Removing that grant denies retrieval.

The reset registration is in its execution transaction; cancellation therefore rolls back that registration as well. These tests do not claim the separate durable-registration behavior used by privileged administrative commands applies to reset consumption.

Prior head `5e051e8f`: retained CI TRX independently reviewed: 968 passed, 3 skipped, 0 failed; all 26 security cases passed. Artifact `sql-integration-regression-evidence`, run 41224819234. The three skips remain separately gated GG-003 historical acceptance cases.

New cases must execute successfully in SQL CI before their acceptance is closed. Compilation and ordinary tests do not establish SQL correctness. No SQL/restored database or production access is used locally. Windows certificate/key recovery, least-privilege SQL, and administrator recovery remain operational acceptance gates. No production latency claim is made.
