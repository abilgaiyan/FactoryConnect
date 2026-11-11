# PR #180 local Edge qualification harness

Implementation only. Neither qualification nor production host has been launched by this change. Restored-SQL chronology PASS and the A1 probe remain accepted; A2/B/C/D are not claimed complete.

This executable composes the production Edge services from the commissioned JSON and Edge appsettings.json defaults, then removes exactly the FactoryConnectWorker MTConnect acquisition hosted-service registration. Inventory, mapping, continuity policy, roster, activity, metric-input, aggregation and operational-projection workers remain. No production source or configuration is modified. No command-line or environment configuration providers are loaded; the exact disposable SQL connection is supplied last by the harness. Demo normalization is rejected.

Allowed target: DESKTOP-1PJK2OB\FCINVENTORY / FactoryConnect_P0_Qualification_20261106 with Windows authentication. Production, the preserved FactoryConnect_P0_20261105 restore, other servers/databases, SQL credentials, attached files and failover partners are rejected. The tool never connects to the preserved restore. Its unchanged-state guarantee is isolation by connection targeting; it does not claim an independent whole-database before/after comparison of that separate database.

The report records process ID, source configuration SHA256 before/after, effective observation settings (no connection strings), hosted-service inventory, exact GG hold diagnostics and timestamped SQL authority position/revision samples for the exact two streams. GG committed authority must stay 513501/revision 4091; 513521 is the offending retained observation, not an advanced checkpoint. A successful run must observe that exact hold. Normal worker failures still trigger host stopping and result in a failed qualification. The top-level catch only writes a failed report and exits nonzero; it does not change worker exception handling.

The tool uses the same persistence startup gate before starting workers; no migration is applied. It runs for 10–120 requested observation seconds, subject to a total cancellation budget, then requests normal host shutdown. SQL sampling is roughly once per second; sampling itself can affect latency. It reports observed progress and its timestamps, not exact internal pipeline-cycle duration or a frozen liveness-threshold PASS. SQL-only/page instrumentation and candidate release/rollback tests remain separate.

Exit codes:
- 0: GG held and both MOXA activity position and revision advanced during this process.
- 3: GG held and host completed, but no MOXA durable advancement. Inconclusive for Gate B, not failure of the accepted chronology correction.
- 1: failure, unexpected shutdown, missing hold, authority drift, configuration change or shutdown error.
- 2: invalid command usage.

No synthetic input is included. The current restored MOXA cursor is 14324/revision 4703 and the supplied query returned no pending moxa001_status observations. An isolated synthetic scenario may be necessary, but is not invented by this tool. Do not reset a checkpoint or edit evidence to produce progress.

## Prepared commands — do not launch until the local run is authorized

Build only (does not start a host):

```powershell
dotnet build tools/FactoryConnect.EdgeQualification/FactoryConnect.EdgeQualification.csproj -c Release
```

After build and guard review, these are two separate process invocations, constituting a complete host stop/start with fresh mapped memory. Use new report filenames for every attempt. Do not substitute a path to a production deployment or copy the supplied config over production files.

```powershell
$tool = 'F:\Development\FactoryConnect\artifacts\bin\FactoryConnect.EdgeQualification\release\FactoryConnect.EdgeQualification.dll'
$config = 'F:\Development\FactoryConnect\factory-backups\config\edge.production.json'
$content = 'F:\Development\FactoryConnect\src\FactoryConnect.Edge'

# Phase 1 — only when authorized for the disposable database:
dotnet $tool $config $content 30 'F:\Development\FactoryConnect\factory-backups\qualification-phase1.json'
# Inspect exit code/report. Exit 3 means no advancement, never Gate B PASS.

# Phase 2 — only after phase 1 completed and shut down:
dotnet $tool $config $content 30 'F:\Development\FactoryConnect\factory-backups\qualification-phase2.json'
```

Compare distinct process IDs and GG baseline/hold in both reports. MOXA must advance in both phases to qualify repeated durable advancement; a first-run catch-up followed by no remaining input is inconclusive for post-restart advancement. Reports do not authorize production activation, merge or evidence resolution. A harness with acquisition removed is a processing-host qualification; it does not test live MTConnect acquisition or the packaged production entry point.

## Implementation validation

Harness build: PASS, zero warnings/errors. Safety tests: 10 passed without constructing/starting a host or opening SQL. Complete Edge test project: 252 passed, 0 failed. The Linux test workspace supplies its missing Asia/Calcutta alias outside the repository. No qualification SQL/host execution was performed; operational acceptance and full-process restart remain pending.
