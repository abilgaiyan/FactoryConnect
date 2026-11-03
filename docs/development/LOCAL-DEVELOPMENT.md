# Local development: restored SQL reporting database

Use this launcher to run the **API, Dashboard gateway, and built React UI** by default against a **physically READ_ONLY** restored SQL Server database. It does not start Edge, MTConnect polling, migrations, fixtures or Vite. It does not modify tracked appsettings files.

From the repository root, use the defaults (SQL instance `DESKTOP-1PJK2OB\FCINVENTORY` and database `FactoryConnect_Gajra_Investigation_20261102`):

```powershell
.\scripts\dev\Start-FactoryConnectLocal.ps1
```

Override the target only when needed:

```powershell
.\scripts\dev\Start-FactoryConnectLocal.ps1 -SqlInstance 'DESKTOP-1PJK2OB\FCINVENTORY' -Database 'FactoryConnect_Gajra_Investigation_20261102'
```

By default, the launcher builds the Dashboard project, which runs `npm ci` and `npm run build` through the existing MSBuild target, then serves the resulting React UI at `http://127.0.0.1:5090/`. The API binds to 127.0.0.1:5080. Use `-ApiOnly` to skip Dashboard and frontend builds. The `-Dashboard` switch remains accepted for compatibility, but is no longer required. Dashboard sources are not automatically populated from SQL: if the UI shows no machines, configure the existing Dashboard source definitions separately.

**Requirements:** Windows PowerShell 5.1 or PowerShell 7+, .NET 10 SDK, Node.js 24.14.1 and npm for Dashboard builds, a restored compatible database in SQL Server READ_ONLY mode, and Windows integrated SQL authentication. For another SQL identity, set a connection string in your shell's environment and pass `-ConnectionStringEnvironmentVariable 'MY_LOCAL_SQL_CONNECTION'`; never paste credentials into the script invocation.

The launcher checks the database identity and READ_ONLY mode, builds the selected project(s), starts only the selected local processes, and waits for basic health. `/health` is a startup signal, **not** proof that report evidence is complete. Ctrl+C ends the session. If the API fails at schema compatibility or the SQL shared migration lock, review console diagnostics; do not bypass the check or write to the database.

**Existing process on port 5080?** Stop it deliberately in its own console first. This launcher refuses to reuse or kill unrelated processes.

Report the canonical query directly against `http://127.0.0.1:5080/api/reporting/v1/operational-metrics/production-day-shifts/query`. A 30-second Dashboard timeout is a separate known P0 performance issue, not a startup failure.

This convenience script is **not** a general permission-isolation solution. READ_ONLY is enforced at the database level, but the authenticated SQL identity may be privileged. Use only local investigation restores; never aim it at live production.

## Local current-state machine inventory

The launcher supplies two API-side MTConnect inventory entries, GG-003 (`677b030b-1ede-4a67-82ae-6a494791357f`) and MOXA-001 (`bfa0dc97-9a68-45ed-b189-1dacb7d863f0`). This is **owner registration only**; neither Edge nor an MTConnect poller is started. BaseUri is a syntactically valid local placeholder, not a proxy connection.

The GG-003 device key defaults to `DEWAS-SR-GG-003` (known activity stream `mtconnect:DEWAS-SR-GG-003`). MOXA-001 defaults to `MOXA-001` as a convenience placeholder **not verified against the restored database's authoritative stream key**. Override the keys if the database uses different device identities:

```powershell
.\scripts\dev\Start-FactoryConnectLocal.ps1 -Gg003DeviceKey 'DEWAS-SR-GG-003' -MoxaDeviceKey '<actual-moxa-device-key>'
```

After startup, query `/api/machines/v1/{machineId}/current-state` for each machine. A `503` can still indicate an unavailable stable authority cut or a policy mismatch. A `200` with stale/no evidence is not a live machine state. The launcher does not synthesize current-state evidence or change the SQL database.
