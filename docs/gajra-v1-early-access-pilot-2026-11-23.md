# Gajra Gears — V1 early-access pilot release gate (23 November 2026)

**Status:** Preparation only. This document does not authorize production deployment, migrations, service restarts, or machine changes.

**Baseline:** V1 `main` after PR #306, commit `778a60eee890d04ed96e26759c63374cea0565a4`.

## Pilot purpose and scope

Give factory supervisors and management a usable evaluation build of existing functionality:
- Machine activity and historical reporting for GG-003 and MOXA-001 where evidence is available.
- Supervisor production entry/review and shift targets, subject to local acceptance.
- Management Daily Report, machine/line/shift filters, print and CSV export.
- Availability, Performance, Quality and OEE only when the reporting evidence qualifies them; otherwise show missing/unavailable/insufficient-evidence and reasons.

This is **not** a claim of complete numerical OEE for both machines. Supervisor declarations must not be silently converted into authoritative CNC quantity or OEE.

## Release go/no-go checklist

| Gate | Required evidence | Initial status |
| --- | --- | --- |
| Source baseline | Record exact `main` SHA and successful CI / MOXA audited recovery checks for that SHA | Pending |
| Release build | Build release package and verify manifest/configuration matches the intended commit | Pending |
| Current deployment | Inventory existing installation, services, scheduled tasks, ports, process paths, site settings and startup ownership | Pending |
| Isolation | Prove pilot API/dashboard cannot accidentally attach to or start a second production Edge/acquisition writer | Pending |
| Backup and rollback | Confirm restorable SQL backup, original deployment/config snapshot and tested return procedure | Pending |
| Database compatibility | Review migration plan and effects on production; no migrations before separate approval | Pending |
| Two-machine reporting | Verify both configured sources, machine identity, shift roster, actual data timestamps, and independent report reads | Pending |
| Supervisor workflow | Test a new/revised entry and target using explicitly approved pilot/test data; inspect SQL persistence and readback | Pending |
| Daily Report | Verify date/line/machine/shift filters, Print, CSV, and missing-vs-zero evidence states | Pending |
| Health and startup | Verify pilot URLs, health endpoints, login and reboot behavior without disturbing existing services | Pending |
| User acceptance | Supervisor and management witness agreed checks and log feedback | Pending |
| Deployment approval | Obtain explicit go-ahead for the exact server, target paths, database, service changes and rollback | Pending |

**NO-GO:** Any unverified shared writer, missing backup, unintended database migration, machine identity mismatch, incorrect numeric OEE, or loss of existing production acquisition.

## Safe sequence

1. Before deployment: capture current production service/process status and machine acquisition baselines; verify repository release build and all gates that can be checked remotely.
2. Obtain exact server/install details and explicit approval for isolated pilot installation.
3. Back up SQL and configuration; preserve existing current installation and its startup ownership.
4. Deploy isolated API/dashboard only if configuration and shared-resource isolation are proven. Otherwise stop and revise the plan; do not launch duplicate Edge services.
5. Verify read-only two-machine reporting first, then supervisor test transactions only with approved pilot data.
6. Record acceptance findings, screenshots/logs, data gaps, and rollback evidence.

## Pilot acceptance record (fill in on site)

- Release commit / artifact:
- Server / pilot install directory / API URL / dashboard URL:
- SQL database / backup location / restore verification:
- Existing acquisition services preserved:
- GG-003 reporting timestamp / state / OEE evidence:
- MOXA-001 reporting timestamp / state / OEE evidence:
- Supervisor entry and target readback:
- CSV/print checks:
- Open issues and evidence gaps:
- Management/supervisor feedback:
- GO / NO-GO and authorizer:

## Deferred from this pilot

Supervisor screen redesign, advanced access-role administration, and numeric OEE for any machine lacking qualified evidence. No data fabrication, silent zero substitution, or unsafe restart is permitted to meet the date.
