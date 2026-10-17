# Gajra Dewas Station Road — commissioning and recovery lessons (16–17 October 2026)

> **Scope:** Evidence-based field runbook for the *new* Dewas Station Road PC `DESKTOP-UV3UD48`, GG-003. Not an authorization for a restart, Windows Scheduled Task, service installation, reboot, database repair, or Shipra plant operation.
>
> **Governing contracts:** [FC-032.4](../features/FC-032.4-factory-demo-deployment-contract.md), [Dashboard LAN guide](factoryconnect-dashboard-lan.md), and the release's `Deploy-FactoryConnect.ps1`. A historical incident is not a substitute for the exact current-release contract.

## 1. Known commissioned topology

| Component | Dewas commissioned value / authority |
| --- | --- |
| PC | `DESKTOP-UV3UD48` (new Dewas Station Road; **not** old Shipra PC) |
| Install root | `D:\FactoryConnect` |
| Permanent MachineId | `677b030b-1ede-4a67-82ae-6a494791357f` |
| DeviceKey | `DEWAS-SR-GG-003` |
| CNC FANUC FOCAS | `192.168.1.101:8193` |
| FANUC SHDR adapter | `D:\MTConnect\fanuc-driver\fanuc.exe`, port `7888` |
| MTConnect Agent | `D:\MTConnect\agent-dewas\agent.exe`, `127.0.0.1:5001` |
| SQL Server | `.\FACTORYCONNECT`, database `FactoryConnect`, service `MSSQL$FACTORYCONNECT` |
| API | `http://192.168.1.59:5080/health` (not `127.0.0.1:5080`) |
| Dashboard | `http://127.0.0.1:3000/health/live` and `/health/ready` |
| Selected release verified 17 Oct | `b3cf4837a1dca43e4702343492a5795cf4c22ade` |

Values above describe this machine's observed configuration, **not portable defaults**. Preserve the existing permanent machine/production identities and `D:\FactoryConnect\config\` files during an upgrade. Do not copy `config-templates` over live site files.

## 2. 16 October incident: two distinct defects

### A. SQL incident / Edge shutdown

- An Edge log contained SQL error **18461**, followed by shutdown. Error 18461 is consistent with a SQL Server single-user-slot connection rejection. The initiating SQL event, duration, and complete shutdown causality were **not established**.
- The later factory check showed SQL `ONLINE`, `MULTI_USER`, and `SERVERPROPERTY('IsSingleUser') = 0`; this proves recovery at check time, not the cause.
- Runtime state `runtime.json` still reported Edge `Owned` and `runtimeRunning=true`, although Edge was absent. API and Dashboard remained running. **A persisted Owned record is not a live process health check.**
- FANUC adapter and Agent continued to run; they are separate from FactoryConnect Edge. No Windows Service or scheduled task had been commissioned to supervise the standalone Edge process.
- On 16 Oct the operator used the **official release deployer** to restore all three application processes, then verified new SQL observations.

### B. Dashboard operational-metrics source mismatch

- The Dashboard source originally used `Dashboard.Sources[0].ProcessorId = machine-state-activity`; this is **not** the operational metric projection identity. The machine-state activity processor must not be renamed to repair reporting.
- Corrected `D:\FactoryConnect\config\dashboard.production.json` source:
  `operational-metrics:677b030b-1ede-4a67-82ae-6a494791357f:builtins-v1`
- Ensure the Dashboard process **actually reloads** commissioned configuration; an on-disk edit alone does not prove its effective setting. Verify `/dashboard/config` and a known historical report, not just HTTP 200.
- Factory UI production-day report, **15 Oct 2026**: Availability **25.353023%**, Utilization **6.569781%**; Performance insufficient (`MissingReferenceTime: IdealProductionDuration`), Quality insufficient (`MissingOperand: GoodQuantity`), OEE insufficient (`DependencyInsufficientEvidence: Performance`). Missing evidence must not be fabricated or converted to zero.
- A prior development snapshot showed Utilization **15.383957%**. This discrepancy is **unresolved**; do not automatically attribute it to the processor fix.

## 3. Deployment lessons (avoid repeating the incident)

1. **Verify the right PC before any command.** The old Shipra host must not be touched. Prefer read-only discovery first.
2. **Transfer a complete ZIP only.** `release.json`, `MANIFEST.sha256`, and scripts are inside its single top-level directory. Do **not** request/copy a separate `release.json`. Looking for `release.json` at ZIP root gives a false negative.
3. **Check exact bytes, not just ZIP filename/commit.** The verified 16 Oct development-path ZIP had length **219283225** bytes and SHA-256 `0452E313E76911288420854B50C23F8CF5E2D23DA1CBA0FEEBB0AB3665F92AA2`. A separately produced ZIP with the same source commit had **different bytes**. Hash equality proves transfer integrity; the deployer separately verifies the release manifest.
4. **Deployer path is at the installed release root**, `D:\FactoryConnect\current\Deploy-FactoryConnect.ps1`, **not** `current\scripts\deployment\Deploy-FactoryConnect.ps1` (that is a source-tree path).
5. **Execution policy:** on this Windows installation, PowerShell script execution was disabled with all policy scopes shown as `Undefined`. The operator ran `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force` for the **current PowerShell process only**, after reviewing `Get-ExecutionPolicy -List`. Do not make a permanent machine policy change as a workaround; organization policy takes precedence.
6. **Deployment is disruptive**, not a plain file copy. `Deploy-FactoryConnect.ps1 -PackagePath <zip> -InstallRoot D:\FactoryConnect` verifies the package, may stop verified-owned application processes, runs migrations, switches the release junction and starts Edge/API/Dashboard. Plan a controlled window; preserve existing `config` and SQL data; do not kill processes or delete `runtime.json` to force recovery.
7. **Partial runtime requires caution.** The observed case was Edge absent but API/Dashboard present. `BootSafe` is a fail-closed *startup mode*, not a guarantee that a deployment will be nondisruptive. Do not conflate the two.
8. **API bind matters.** The Dewas API listened on `192.168.1.59:5080`; `http://127.0.0.1:5080/health` failed despite a healthy API. Check `api.production.json` and `Get-NetTCPConnection` before diagnosing an outage.
9. **SQL health has layers.** SQL service `Running`, a database `ONLINE/MULTI_USER`, a successful operator `sqlcmd -E`, and Windows `NT AUTHORITY\SYSTEM` runtime access are **different proofs**. SYSTEM DB read/write permissions were granted as a commissioning concession and should be least-privilege-reviewed; do not silently broaden server permissions.
10. **Verify ingestion, not merely process/HTTP health.** The GG-003 `MachineObservation` count/position increased from **11170** to **11205** across two samples on 16 Oct, and reached **25954** with latest timestamp `2026-10-17 04:15:11.6128450 +00:00` at the next morning check. This establishes real durable raw observations, not proven accepted good-part production.
11. **No reboot-safe startup activation yet.** FANUC/Agent are interactive processes; `Start-Acquisition.bat` and scheduled-system startup were not commissioned. `Start-Dewas-GG003-Fanuc.bat` runs the adapter in a blocking mode and cannot be substituted as an unattended launcher without completing acquisition ownership and SYSTEM conformance gates.

## 4. Read-only daily health commands (NEW Dewas PC only)

~~~powershell
if ($env:COMPUTERNAME -ne 'DESKTOP-UV3UD48') { throw 'Wrong computer' }
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -in @('fanuc.exe','agent.exe','FactoryConnect.Edge.exe','FactoryConnect.Api.exe','FactoryConnect.Dashboard.exe') } |
  Select-Object Name,ProcessId,ExecutablePath
Get-Service -Name 'MSSQL$FACTORYCONNECT'
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object { $_.LocalPort -in @(7888,5001,5080,3000) } |
  Select-Object LocalAddress,LocalPort,OwningProcess
foreach ($url in @(
 'http://127.0.0.1:5001/probe',
 'http://192.168.1.59:5080/health',
 'http://127.0.0.1:3000/health/live',
 'http://127.0.0.1:3000/health/ready')) {
 try { "$url : $((Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5).StatusCode)" }
 catch { "$url : FAIL $($_.Exception.Message)" }
}
~~~

For durable observations run twice about 30–60 seconds apart **while new source observations are expected**:

~~~powershell
sqlcmd -S '.\FACTORYCONNECT' -d FactoryConnect -E -C -b -l 10 -W -Q "
SELECT COUNT_BIG(*) AS TotalObservations, MAX(ObservedAt) AS LatestObservedAt,
       MAX(Position) AS HighestPosition
FROM dbo.MachineObservation
WHERE MachineId = '677b030b-1ede-4a67-82ae-6a494791357f';"
~~~

Check for count/position increase and freshness. A stationary counter alone is not sufficient to diagnose a failure without knowing the acquisition/source activity. These commands are read-only.

## 5. Recovery and acceptance boundaries

- If Edge is missing, gather process inventory, runtime state, SQL readiness and recent Edge/application logs **before** taking corrective action. Do not trust stale `runtime.json` alone.
- Stop on official deployment failure; inspect the attempt's `deployment-failure.json` and migration logs. **Do not blindly rerun** after partial shutdown or migration.
- Preserve `D:\FactoryConnect\config\`, `deployment` evidence, SQL records, machine identity, and running FANUC/Agent. Do not alter Shipra.
- After recovery require: selected release/status; live Edge/API/Dashboard processes; HTTP 200 at the **configured** addresses; Agent/SHDR listeners bound to intended processes; at least two samples of durable SQL observations; known-date Dashboard metric report that returns real values; clear distinction between provisional/missing OEE evidence.
- The **17 Oct 09:45 IST** check: all five relevant processes running, SQL service running, Agent/API/Dashboard HTTP 200, ports 7888/5001/5080/3000 listening on intended PIDs, and SQL observations at 25954 with a fresh observed timestamp. This establishes live operation at that time, **not** Windows reboot or unexpected-process-stop recovery.

## 6. Open actions

- Investigate SQL error 18461 origin using retained SQL Server and Edge logs; do not assert definitive initial causality without evidence.
- Reconcile the different 15 Oct historical Utilization results with exact query scope/revisions.
- Commission unattended **SYSTEM** acquisition bootstrap, runtime startup and scheduled task only after separate approved tests; **no uncontrolled reboot**.
- Obtain factory-approved ideal durations, GoodQuantity/reject policy evidence and production scope before declaring Performance, Quality or OEE available.
