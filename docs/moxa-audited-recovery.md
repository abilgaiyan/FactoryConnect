# MOXA-001 audited pre-checkpoint recovery — review only

Production HOLD. This PR authorizes no factory execution. A production-derived
clone rehearsal, CI review, exact release/artifact hashes and a separate approved
maintenance window are required. GG-003 acquisition and processing pause because
GG-003 and MOXA share Edge; API/Dashboard remain owned and health checked.

## Fixed scope and schema

Migration 023 adds ProductionAdmissionRecoveryAudit under the normal repository
migration ledger/schema descriptor. It does not change admission policy. Do not
apply ad hoc DDL or modify immutable releases. Deploy the matching runtime/schema
through the reviewed release process; an older runtime is not assumed compatible.
The dedicated tool fixes row 2, machine bfa0dc97-9a68-45ed-b189-1dacb7d863f0,
stream mtconnect:DEWAS-SR-MOXA-001, from bound/null to bound/2026-10-25T02:00Z.
Row 4, source observations/activity, schedules and all checkpoints are unchanged.
The normal runtime binding remains immutable.

The transaction takes the shared migration lock and verifies current schema,
locks the exact processor and missing checkpoint range, verifies all output
identity/payload hashes and typed JSON ownership, and follows eligibility source
references. Unsupported, malformed, duplicate-property or ambiguous outputs abort.
Metric stream/fact/aggregate/projection absence is protected with SERIALIZABLE
predicate locks. Trusted foreign keys connect contributions, revisions, projection
evidence and coverage to these parents. Immutable assessment ownership is decoded
with application codecs. Global output scans can block GG-003 reporting; bounded
SQL timeouts abort rather than weakening integrity.

Audit insertion and binding update share one transaction. An internal test seam
injects failure between them; no production CLI fault switch exists. A replay
fails closed; it never clears an audit, resets admission or advances a checkpoint.
After a lost commit acknowledgement, inspect the audit by operation ID before
any next action. Never automatically restore the old binding/configuration.

## Maintenance operation

Use the PR's matching Restart-FactoryConnectEdge.ps1 and helpers from a reviewed
operations bundle, not stale installed script copies. Inventory all installed
entry points and disable/repoint obsolete launchers before execution. Every
FactoryConnect deploy/start/restart path must use the deployment lock and honor
moxa-maintenance.intent.json. Independent Windows services must be disabled;
scheduled tasks/external supervisors/manual launchers must be suspended and
recorded in ExternalLaunchersSuspendedReference. The wrapper rejects detected
active service launchers. An arbitrary external launcher cannot be proven absent
by a SQL transaction or a filesystem lock. This commissioning prerequisite must
be reviewed against the actual server; the script does not claim otherwise.

The optional MoxaRecoveryPlan parameter on Edge restart selects the maintenance
path. The existing operation retains deployment.lock continuously from ownership
preflight through shutdown, SQL correction, configuration activation and restart.
A persistent marker is published before termination. It blocks cooperating
operations even if this wrapper crashes and releases its file handle. Failure
preserves the marker, runtime failure/intent evidence and original config backup.
No automatic retry, marker deletion, restart or adoption of unrecorded processes.
The wrapper rechecks global Edge absence before calling SQL. The tool executable
hash is verified; review the complete published artifact/manifest as well.

A plan JSON contains OperationId (new GUID), ExpectedServer, ExpectedDatabase,
ApprovalReference, ExternalLaunchersSuspendedReference, RecoveryExecutable (absolute
path to the reviewed self-contained EXE), RecoveryExecutableSha256 (uppercase).
Credentials come from the commissioned Edge configuration and are passed only in
a temporary process environment, not arguments or audit. Restrict permissions on
maintenance logs/config backups, which contain the original configuration.
The matching MOXA boundary is generated in memory and activated only after SQL
commit; all other configuration values are retained. The audit captures the
maintenance evidence, selected release, old Edge identity and original config hash.

No production invocation command is supplied until clone rehearsal and operational
review. For development, build first, then run:

```powershell
dotnet build FactoryConnect.sln -c Release
dotnet test tests/FactoryConnect.Integration.Tests -c Release --no-build --filter "FullyQualifiedName~MoxaAdmissionRecoveryIntegrationTests"
powershell -NoProfile -File tests/deployment/Invoke-MoxaMaintenanceConformance.ps1
```

SQL tests require FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING and create
isolated disposable databases; never target factory production through the tests.
CI runs the SQL tests in the existing integration suite and the maintenance
conformance/publish checks in moxa-recovery.yml. Mocked maintenance tests do not
establish Windows executable/process or production acceptance.

## Acceptance and failure review

On a clone: exercise normal correction, row/key/policy/checkpoint mismatches,
existing outputs and unsupported ownership, audit rollback, competing writer,
timeout, replay and lost-ack inspection. Rehearse wrapper interruption before/after
SQL commit, config activation and process launch; retain the persistent marker
until a separately reviewed reconciliation proves the outcome. Do not delete it
merely to make restart proceed.

After approved production activation: prove checkpoint advancement, before-boundary
empty-output consumption, crossing interval clipping, and no activity-derived
outputs before 02:00 UTC. Confirm row 4 binding/configuration unchanged and GG-003
historical reporting accepted, preserving insufficient-evidence classifications.
Missing shift coverage after the boundary remains an error; recovery does not
prove that the configured admitted-time shift coverage is complete.
