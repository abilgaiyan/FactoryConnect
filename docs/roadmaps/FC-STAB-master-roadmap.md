# FactoryConnect — FC-STAB Master Stabilization Roadmap

**Baseline:** 2026-10-21  
**Scope:** Gajra Dewas Station Road GG-003; deployment, configuration, operational metrics and Phase-1 reporting.  
**State:** Approved tracking baseline; **not** authorization for production changes.  
**Rule:** Evidence → RCA → approved fix → automated tests → isolated rehearsal → controlled production verification → closure.

## 0. Safety and operating decisions

- Preserve the existing production database, raw CNC observations, release, configuration backups and incident logs. No destructive reset, speculative replay, historical rewrite or restart without a validated recovery plan.
- Continue healthy acquisition. The observed Edge-only restart script **stops the existing Edge before launching its replacement**; it is not a read-only test.
- Previous production SQL permission covered a narrowly scoped historical extraction only. No further production SQL exploration without separate authorization.
- Do not backdate schedule changes without an explicit factory-day and historical-recomputation decision.
- Distinguish **Confirmed**, **Unresolved**, **Hypothesis**, and **Proposed**. HTTP 200 or a merged PR does not prove reporting correctness.
- A clean deployment may be rehearsed with a separate disposable database; never replace production as a diagnostic shortcut.
- Keep the architecture controller-agnostic; FANUC/MTConnect is the current proving case.

## 1. Evidence baseline

| ID | Observation | Classification / limitation |
|---|---|---|
| EV-01 | FANUC → MTConnect → durable raw SQL ingestion verified; 253,291 observations at 21 October sample | Confirmed at sampled times; does not prove reporting health |
| EV-02 | Deployed release `7e0a67fb2fe01aa6c0f1c7d803c5bc7badc23306`; developer HEAD `93bc03c582375c855f1aadc53a6d95afea852a2c` | Confirmed; runtime source and configuration still need reconciliation |
| EV-03 | Actual shifts IST: S1 07:30–16:00 (break 11:30–12:00), S2 16:00–00:00 (20:00–20:30), S3 00:00–07:30 (03:30–04:00) | User-attested; net 8h, 7.5h, 7h if fully planned |
| EV-04 | Production JSON configures S1 06:00–14:00, S2 14:00–22:00, S3 22:00–06:00, effective 2026-10-15 | Confirmed mismatch; not proven cause of incident |
| EV-05 | Production `ProductionProcessing.Machines[0]` lacks `Contexts` and `PlannedProduction` | Confirmed in JSON; effective runtime configuration unknown |
| EV-06 | Deployed-commit source requires at least one context and `GetRequiredSection("PlannedProduction")` when reader executes | Confirmed source contract; conditional activation unresolved |
| EV-07 | Edge restart projects JSON to environment and stops the owned Edge before launching a replacement | Confirmed script excerpts; rollback not proven |
| EV-08 | Core resolver supports multiple planned windows and subtracts breaks; Edge JSON reader constructs only one | Confirmed source behavior |
| EV-09 | `IsOvernight => EndsAtLocal < StartsAtLocal`; `00:00–00:00` resolves to zero duration, not 24h | Confirmed; prohibited workaround |
| EV-10 | Edge logs show `Processor gate acquisition timed out` in containment publication/replay | Confirmed symptom; RCA unknown |
| EV-11 | Shift report requests cancel at ~30 seconds (HTTP 499); production-days request also took ~28 seconds | Confirmed log samples; SQL contention not established |
| EV-12 | Historical revision 534: Running 2683.614157 s, Planned 2452.013355 s, Availability 109.45% | Confirmed anomaly; semantic RCA not established |
| EV-13 | Historical Running and Planned evidence for SHIFT-1 have zero temporal overlap in reviewed sample | Confirmed sampled evidence; not proof of entire causal chain |
| EV-14 | API/Dashboard health endpoints responded HTTP 200 while reporting was impaired | Confirmed; health != functional correctness |
| EV-15 | Existing `edge.production.before-shift-correction-20261021.json` backup SHA256 matched original | Confirmed; preserve backup |

## 2. Master issue register

**Statuses:** OPEN = established work; INVESTIGATE = symptom established, cause unknown; DESIGN = proposal requiring approval; HOLD = prerequisite missing; CLOSED = acceptance evidence attached. No item is closed by this document.

| ID | Priority | Status | Issue | Closure evidence | Depends on |
|---|---|---|---|---|---|
| STAB-01 | P0 | INVESTIGATE | Effective configuration/startup path inconsistent with required machine sections | Exact release call graph, activation gates, provider precedence, redacted effective-key proof | None |
| STAB-02 | P0 | OPEN | Restart lacks proven fail-before-stop preflight | Validate replacement without stopping Edge; rollback drill | STAB-01 |
| STAB-03 | P0 | INVESTIGATE | Shift reports cancel at ~30s; broad reporting latency | Restored-data reproduction, traced cause, correct bounded response | STAB-01/04 as applicable |
| STAB-04 | P0 | INVESTIGATE | Operational metric processor-gate timeouts | Concurrency/transaction RCA, deterministic tests, no partial publication | Isolated restore |
| STAB-05 | P1 | OPEN | Factory shift times incorrect in production JSON | Approved effective-dated S1/S2/S3 and boundary tests | STAB-01/06 |
| STAB-06 | P1 | DESIGN | Edge JSON reader cannot configure multiple planned windows | Approved backward-compatible schema, midnight/overlap/break tests | STAB-01 |
| STAB-07 | P1 | INVESTIGATE | Machine `Contexts` and `PlannedProduction` absent in JSON | Reconcile effective config, approved assignment IDs/dates, startup validation | STAB-01 |
| STAB-08 | P1 | INVESTIGATE | Revision 534 Availability >100% | Exact provenance and semantic RCA, independent fix verification | Isolated restore |
| STAB-09 | P1 | OPEN | End-to-end reporting reconciliation incomplete | CNC → contributions → revisions → report evidence against approved definitions | STAB-03/04/05/08 |
| STAB-10 | P2 | OPEN | Diagnostics dispersed across scripts/logs | Redacted read-only diagnostics bundle and actionable failure codes | STAB-01 |
| STAB-11 | P2 | OPEN | No single validated multi-component deployment contract | Versioned manifest, schema/dependency checks, clean-room test | STAB-02/10 |
| STAB-12 | P1 | OPEN | Phase-1 customer reports not signed off | Performance, utilization, OEE index, monthly utilization mapping and provisional labels signed off | STAB-09/customer |
| STAB-13 | P1 | INVESTIGATE | Production-day/shift boundaries ambiguous at midnight | Approved factory-day convention and date-scoped fixtures | STAB-05/06 |
| STAB-14 | P2 | OPEN | Runtime release/config provenance difficult to establish | Commit, config digest, processor identities, schema and launch source exposed | STAB-01/11 |
| STAB-15 | P1 | OPEN | Recovery/rollback acceptance not demonstrated end-to-end | Restore/restart interruption drills, ingestion and reporting integrity checks | STAB-02/11 |

## 3. Gated execution roadmap

### G0 — Evidence freeze and production protection
Preserve release identity, logs, configuration backup, SQL history and current process inventory. Establish exact change approval and recovery boundaries. **Exit:** evidence indexed, no pending risky operation.

### G1 — Configuration truth and activation path
Trace deployed-commit service registration and feature gates; inspect `appsettings*`, environment projection, provider precedence and conditional processing activation. Explain how Edge runs with absent JSON sections. **Exit:** STAB-01 explained with source/runtime evidence, not inference.

### G2 — Read-only deployment doctor
Implement `Test-FactoryConnectDeployment.ps1` or equivalent: release identity, required keys, stream mappings, timezone, shift and planned-window validity, SQL provider/schema, endpoints, ownership and rollback readiness. No secret disclosure, SQL mutation or process stop. **Exit:** known invalid GG-003 config fails clearly, known-good isolated config passes, CI regression passes.

### G3 — Factory scheduling and context correction (isolated)
Approve factory-day convention, context assignment authority and effective dates. Support three planned windows in backward-compatible Edge configuration if needed. Test S1/S2/S3, breaks, midnight, equal start/end, overlaps, holidays, effective-date changes and replay isolation. **Exit:** controlled fixture yields 8h/7.5h/7h net time without unapproved historical changes.

### G4 — Reporting RCA and bounded recovery (isolated)
Separate work packets for processor-gate contention, HTTP 499/latency and revision-534 Availability. Reproduce first, fix proven causes, test concurrency, exact processor identity, idempotence and immutable evidence. **Exit:** failing tests turn green and restored GG-003 reports are correct and bounded.

### G5 — Clean-room deployment rehearsal
Use disposable SQL database, versioned manifest, explicit configuration template, secrets procedure, migrations, adapter/agent/Edge/API/Dashboard startup, restart and rollback tests. Do not disturb real CNC acquisition. **Exit:** another operator obtains repeatable PASS without undocumented settings.

### G6 — Controlled Gajra production change
Require G0–G5 acceptance, approved maintenance window, backups, tested rollback, exact release/config digests. Preflight, deploy, transition only approved components, verify ingestion, durable checkpoints, reports and latency. **Exit:** signed acceptance or rehearsed rollback; HTTP 200 alone is insufficient.

### G7 — Customer Phase-1 signoff and scale-out
Validate report definitions, factory-day semantics, machine/line/shift totals, provisional Quality/OEE labeling, handover and repeatable next-machine deployment. **Exit:** customer-accepted reports and operations runbook.

## 4. Work-in-progress discipline

1. One primary gate at a time; parallel investigation only if isolated and non-disruptive.
2. Every task states issue ID, evidence, falsifiable hypothesis, scope, forbidden operations, acceptance tests and rollback.
3. No implementation for unproven RCA; hypotheses stay labeled.
4. Start each session by reading this file and naming current gate, last accepted evidence and next action; end by updating statuses and evidence.
5. No new features, machine rollout or generalized redesign until G6 stable unless explicitly authorized.
6. New findings receive stable IDs and triage; they do not silently replace current work.
7. PR merged != production incident closed; closure requires deployed verification.

## 5. First work packet — FC-STAB-G1.1

**Goal:** Explain why Edge runs while JSON lacks machine `Contexts` and `PlannedProduction`.

**Development-only, read-only:** Trace exact deployed-commit call sites for `ReadMachineConfigurations`, service registration and activation predicates; inspect config provider order, appsettings and launcher projection.

**Server read-only only if necessary:** redacted key presence, running release/start time, approved log excerpts. Never print secrets, connection strings or complete process environment.

**Acceptance:** Evidence-backed decision tree showing which code path executed, which provider supplied required keys (if any), and why startup succeeded. If unproven, remain INVESTIGATE.

**Forbidden:** JSON edit, Edge restart, production SQL, projection replay, database replacement.

## 6. Gajra stakeholder statement

Real FANUC CNC observations have been acquired and durably stored. Current priority is production reliability: configuration validation, correct shifts and trustworthy reports. Existing history will be preserved while fixes are tested separately. Do not claim OEE/Phase-1 reporting is production-ready before acceptance.

## 7. Change log

- **2026-10-21:** Initial evidence-based stabilization baseline; 15 issues, G0–G7, G1.1 as first investigation. Documentation only; no production change.
