# Reusable Windows startup supervisor (development candidate)

This feature is installation-neutral: no customer, site or machine names are embedded in scripts. All acquisition sources require explicit authorization in a local configuration.

## Contents
- `Watch-FactoryConnectStartup.ps1`: repeated supervision, or `-Once` for a single active recovery cycle; `-CheckOnly` for a read-only, single-cycle diagnostic (no process launches or log writes).
- `Install-FactoryConnectStartup.ps1`: preview by default; `-Install` registers an elevated SYSTEM task at boot.
- `startup.config.example.json`: disabled example, not production-ready.

## Usage
Copy the example configuration to a site-local JSON file; verify executable paths, command lines, health endpoints and commissioning permissions. In elevated PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-FactoryConnectStartup.ps1 -ConfigPath .\startup.config.json
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Watch-FactoryConnectStartup.ps1 -ConfigPath .\startup.config.json -CheckOnly
# One active recovery cycle (may launch processes):
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Watch-FactoryConnectStartup.ps1 -ConfigPath .\startup.config.json -Once
# Only after reviewing scripts and local configuration:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-FactoryConnectStartup.ps1 -ConfigPath .\startup.config.json -Install
```

The installer does not start the task or change existing tasks. Installation refuses recognized existing FactoryConnect startup tasks; verify any differently named custom tasks manually before installing. Test as SYSTEM on a development machine and validate data freshness, restart, rollback, and permissions before production deployment.

## Known limitations
This is a process-presence and HTTP-level supervisor, not a full ingestion-freshness monitor. A partially running application is left untouched for investigation. Runtime launcher is invoked only when all three app processes are absent. Task Scheduler retries a failed supervisor, but cannot by itself prove recovery of every acquisition process. Do not replace a running site-specific supervisor before a planned maintenance window.
