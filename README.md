# FactoryConnect

Industrial machine connectivity and factory data platform built with .NET.

## Vision

FactoryConnect provides a reliable factory-data foundation between industrial machines and higher-level business, analytics, and AI applications.

```text
Machine
  ↓
I/O Gateway / Industrial Protocol
  ↓
FactoryConnect Edge
  ↓
Durable Raw Observations
  ↓
Canonical Observations
  ↓
Durable Machine State / Activity
  ↓
Historical Production Context + Shift + Planned Production
  ↓
Durable Metric-Input Facts
  ↓
Durable Shift / Production-Day Aggregates
  ↓
Versioned Operational Metric Evaluation
  ↓
Durable Operational Metric Projections
  ↓
Provider-Neutral Reporting
  ↓
Applications / Analytics / AI
```

## Current Capabilities

- Canonical machine and observation contracts
- Modbus TCP and MTConnect protocol adapters
- MTConnect discovery, current and sequence-aware sample acquisition
- Continuous Edge acquisition with transient retry and continuity recovery
- Durable observation ingestion with atomic cursor checkpointing
- Pluggable persistence-provider selection with declared provider capabilities
- In-memory persistence provider with full FC-027 operational-metric support
- SQL Server persistence with transactional ingestion, durable observation processing, production reporting, operational-metric projection/query persistence, and authoritative current-state support
- Durable raw-to-canonical observation processing with independent checkpoints
- Durable machine state changes and activity-period projection
- Effective-dated production context with company, site, line, machine, order, operation, part, and operator dimensions
- Flexible recurring shift schedules with overnight shifts, calendar overrides, line precedence, and deterministic DST handling
- Context/shift interval allocation with deterministic lineage and duration conservation
- Planned-production windows, breaks, shutdowns, and replacement overrides
- Durable planned-production eligibility facts
- Durable duration and explicit quantity metric-input facts with replay-stable identity
- Independent activity and quantity processors with atomic output/checkpoint commits and restart/replay conformance
- Durable shift and production-day metric aggregation
- Versioned operational metric definitions with exact-version dependency graphs
- Deterministic Availability, Utilization, Performance, Quality, and OEE evaluation
- Coherent exact-revision metric evaluation with full-precision dependency composition
- Durable operational metric projections with atomic checkpointing, replay manifests, and recursive evidence lineage
- Provider-neutral shift and production-day reporting readers with lightweight summaries and exact-version detail
- Versioned HTTP reporting APIs for shift, production-day, and production-day-to-shift queries
- Authoritative current-machine-state reading over acquisition/contact, mapping-coverage, and state/activity continuity authorities
- In-memory and SQL Server current-state authority realization with stable authority-cut semantics
- Versioned HTTP current-state surface with explicit evidence/no-evidence, coverage, freshness, and usability semantics
- React/TypeScript factory dashboard with production-day, shift-performance, daily-report/print, and machine current-state surfaces
- Same-origin dashboard gateway with generated OpenAPI-derived browser contracts
- Shared persistence, processing, API, and presentation conformance tests

## Technology

- C#
- .NET 10
- ASP.NET Core
- Worker Services
- Microsoft.Data.SqlClient
- SQL Server
- Modbus TCP
- MTConnect
- xUnit
- React + TypeScript

## Architecture Principles

1. FactoryConnect owns the factory and machine domain.
2. Protocols are adapters and must not leak into the domain model.
3. Machine signals are translated into canonical observations and machine state.
4. Acquisition continuity and durable persistence are explicit architectural boundaries.
5. Production context is resolved historically for the interval being processed, not from current configuration.
6. Temporal allocation uses deterministic half-open intervals and preserves duration and lineage.
7. Metric inputs are durable evidence facts; operational metrics are derived from durable aggregates rather than raw observations.
8. Metric definitions are versioned contracts; dependent metrics reference exact definition versions.
9. Operational metric evaluation uses one coherent FC-026 source revision and applies rounding only at the durable projection boundary.
10. Persistence providers are replaceable; available providers are not automatically active providers, and a provider must declare every capability required by the composition root.
11. The Edge runtime must operate independently of the dashboard UI.
12. Hardware is replaceable through connector abstractions.
13. The software must be testable without physical factory hardware.
14. PulseStackAI is a separate platform and may consume FactoryConnect data for AI orchestration.

## Persistence

FactoryConnect selects exactly one persistence provider at the composition root.

```json
{
  "Persistence": {
    "Provider": "InMemory"
  }
}
```

For SQL Server, provider-specific configuration is supplied separately:

```json
{
  "Persistence": {
    "Provider": "SqlServer"
  },
  "PersistenceProviders": {
    "SqlServer": {
      "ConnectionString": "<connection-string>"
    }
  }
}
```

Provider registration remains separate from provider activation. SQL Server configuration is validated only when SQL Server is selected.

Persistence providers also declare the capability set they implement. Composition roots activate only the capabilities they require and fail explicitly when the selected provider cannot supply them.

The in-memory and SQL Server providers both participate in the delivered production-reporting and authoritative current-state paths. SQL Server now supports durable observation processing, operational-metric projection/query persistence, the current-state authority graph, and stable current-state authority-cut reading in addition to the earlier core persistence contracts.

## SQL Server Deployment Prerequisite

A production SQL Server database must be provisioned and migrated through the repository-owned migration path before starting a SQL-backed FactoryConnect runtime. Supply its connection string through normal .NET configuration or a secret store.

Runtime hosts verify migration history and current schema compatibility but do not perform deployment DDL. Database provisioning, migration execution, credentials, and infrastructure policy remain deployment responsibilities.

## Initial Deployment Scope

The first deployment scope targets industrial machine connectivity through Ethernet-capable controllers and retrofit I/O gateways, with MTConnect and Modbus TCP feeding the same canonical FactoryConnect model.

## Project Status

FactoryConnect has progressed through **FC-031 — Authoritative Current Machine State**.

The delivered platform now spans durable acquisition and observation processing; production context, shift, planned-production and metric-input persistence; shift/production-day aggregation; exact-version operational metric evaluation; SQL-backed reporting persistence; versioned HTTP reporting APIs; the React/TypeScript dashboard and daily report; and authoritative current-machine-state consumption from acquisition authority through the machine-detail presentation.

FC-031 closes the current-state path through acquisition/contact, mapping-coverage and state/activity continuity authorities, a stable authority cut, the Core semantic reader, InMemory and SQL Server realization, `GET /api/machines/v1/{machineId}/current-state`, and configured-machine dashboard consumption. Current-state reading is request-driven. Initial load and manual refresh are delivered; polling, streaming, push updates, and background refresh remain optional future product work.

See `docs/features/FC-028-reporting-api.md`, `docs/features/FC-029-first-factory-dashboard-daily-report.md`, `docs/features/FC-030-sql-production-reporting-persistence.md`, and `docs/features/FC-031-authoritative-current-machine-state.md` for the corresponding feature boundaries and closure records.

Cross-machine/site rollups, downtime-reason workflows, manual backfill/re-evaluation, alerting, predictive metrics, and AI integration remain separate future work.

## License

To be defined.