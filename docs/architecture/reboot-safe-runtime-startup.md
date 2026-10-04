# Reboot-safe FactoryConnect runtime startup

RBS-01 through RBS-06 are frozen. The start-only operation uses the selected `current` junction and existing commissioned configuration. It does not install a release, change selection, run migrations, create commissioning configuration, or mutate machine data.

Ownership identity is PID + exact executable path + UTC process start time. Edge, API and Dashboard are completely classified before mutation. Any mismatch fails closed. Every verified-owned process is revalidated immediately before termination.

Reconciliation: all three Owned and healthy => `AlreadyRunning`; all Absent => canonical startup; partial Owned or all Owned with failed required health => stop only revalidated Owned processes and canonical startup; any Mismatch => stop nothing and start nothing.

Canonical startup is Edge, stabilization and ownership proof, API and `/health`, Dashboard and `/health/live` plus `/health/ready`, then final ownership verification. The operation shares `deployment.lock` with deployment. Startup failure cleans up only processes started by that attempt, with ownership revalidation.

B01-B12 remain executable acceptance requirements. Static repository conformance is supplemental and does not replace Windows scenario execution.

## Repair acceptance

The process-creation intent is committed before launch and removed after atomic
ownership publication. In a failure handler, an intent is cleared only while this
attempt holds the lock, the persisted attempt identity matches, and an exact
record proves the launched process is absent. An unknown launch outcome or failed
cleanup keeps the intent and requires reconciliation; entering `catch` is never
sufficient proof. Preflight failures preserve existing runtime evidence.

`Invoke-RebootStartupIntentConformance.ps1` executes the production failure handler
under six injected outcomes: unknown launch, failed cleanup, verified absence,
foreign intent, lock loss, and failed publication. These supplement B01–B12.

`Invoke-RebootStartupExecutableConformance.ps1` requires Windows PowerShell and
permission to create junctions and local HTTP listeners. Its disposable compiled
Edge/API/Dashboard fixtures own their health responses and use dynamically selected
ports. B01–B12 retain their frozen meanings: clean start, no-op, stale reboot
records, Edge stabilization failure, API health failure, Dashboard live/readiness
failure, PID identity mismatch, missing current, invalid current, invalid/missing
commissioning, lock contention, and partial/unhealthy runtime reconciliation.
B11 additionally verifies unresolved-intent retries and lock-loser preservation.
Every child launched from a disposable fixture root is revalidated and terminated
before that root is removed, including children absent from runtime evidence.

No scheduled task, merge, or factory deployment is included in this repair.

### RBS-07 — Cross-operation unresolved-intent exclusion

Deployment and startup share `deployment/deployment.lock`. After acquiring that
lock, deployment rejects the presence of `deployment/runtime-start.intent.json`
before package inspection/extraction, release installation, configuration or
selection changes, migrations, or process operations. Deployment does not parse,
reconcile, or remove the intent. Existing intent, runtime evidence, commissioned
configuration and selection remain unchanged; diagnostic failure logs are allowed.
Startup retains ownership of recovery.

Run the executable cross-operation proof in Windows PowerShell 5.1:

```powershell
.\tests\deployment\Invoke-RebootStartupDeploymentExclusionConformance.ps1
```

The proof launches an unrecorded disposable child, leaves an unresolved intent,
and invokes the real deployer in a separate process. It requires non-zero rejection
at `UnresolvedStartupIntent`, unchanged protected file hashes and selection, and
unchanged child ownership. It also checks that migration never starts and the
lock is released. Its package sentinel deliberately exercises rejection before
package validation; it does not claim a disposable-SQL integration test. Existing
Scenario 4 and SQL regression requirements remain separate acceptance gates.
