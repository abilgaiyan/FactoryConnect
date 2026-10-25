# Acquisition evidence verification: A1/A2 comparison

This change keeps the existing acquisition transaction, lock hints, publication order,
V1 evidence format and full predecessor-prefix verification. No production action is included.

## A1 instrumentation

The `FactoryConnect.Acquisition` ActivitySource is opt-in. Enable its Edge log sink
before launching the development Edge process:

```powershell
$env:AcquisitionDiagnostics__Enabled = 'true'
[Environment]::SetEnvironmentVariable('Logging__LogLevel__FactoryConnect.Acquisition.Timing', 'Information', 'Process')
```

Each acquisition commit has a trace ID. Nested spans record transaction open/lifetime,
reconciliation, raw-state writes, evidence append, prefix reading/decoding/hash checks,
historical source verification, commit and rollback. Source-verification spans record
publication count, observation-occurrence count and actual attempted SQL command count.
A missing `Completed=True` means that stage did not reach its success boundary.
No SQL text, connection strings, observation values or evidence payloads are logged.

Transaction lifetime is not exclusive-lock duration: raw-row X locks start at inserts
and remain through commit/rollback. The raw-write start through transaction end is an
upper bound on the first raw-row X-lock interval, not a direct lock measurement.
Do not add nested span durations to their parent duration.

## Repeatable comparison

Run the same SQL integration workload on the A1 commit and on A2, with the same
SDK, SQL instance, machine resources, configuration and logging settings. Restore
NuGet dependencies first. The tests use disposable migrated databases and output
command counts and timings through the test runner; they do not use the factory DB.

```powershell
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter 'FullyQualifiedName~SqlServerAcquisitionPersistenceDiagnosticsTests' --logger 'trx;LogFileName=acquisition-diagnostics.trx'
```

Record full request/transaction timings and source SQL counts independently. Compare
matched cold/warm runs and report memory pressure alongside durations. No runtime
performance result is established merely by source inspection or passing assertions.

A3 clone validation must reproduce the preserved historical acquisition workload on
an explicitly disposable restored GG-003 database, preserving hashes, identities,
checkpoints, contact authority, replay and corruption rejection. Capture concurrent
observation-reader completion, SQL waits and lock duration as well as transaction time.
No clone or factory database is changed by these instructions automatically.
