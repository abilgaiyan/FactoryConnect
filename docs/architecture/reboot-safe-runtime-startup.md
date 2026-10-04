# Reboot-safe FactoryConnect runtime startup

Contract: RBS-01 through RBS-06 are frozen.

The start-only operation uses the selected `current` junction and existing commissioned configuration. It does not install a release, change selection, run migrations, create commissioning configuration, or mutate machine data.

Ownership identity is PID + exact executable path + UTC process start time. Edge, API and Dashboard are completely classified before mutation. Any mismatch fails closed. Every verified-owned process is revalidated immediately before termination.

Reconciliation policy:

- all three Owned and healthy: `AlreadyRunning`, no-op;
- all Absent: canonical startup;
- partial Owned, or all Owned with failed required health: stop only revalidated Owned processes and perform canonical startup;
- any Mismatch: stop nothing, start nothing.

Canonical startup is Edge, stabilization and ownership proof, API and `/health`, Dashboard and `/health/live` plus `/health/ready`, then final ownership verification.

The implementation shares `deployment.lock` with deployment to prevent concurrent runtime/deployment mutation. Startup failure cleans up only processes started by that attempt, subject to the same ownership revalidation rule.

B01-B12 remain the executable acceptance matrix; repository static conformance is supplemental and does not replace Windows scenario execution.
