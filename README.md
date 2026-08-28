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
- SQL Server core persistence provider with transactional commits, idempotent replay, exact stream identity, and same-stream concurrency protection
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
- Shared persistence and processing conformance tests

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
- React + TypeScript (planned)

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

Persistence providers also declare the capability set they implement. The full Edge application currently requires the FC-027 operational-metric capabilities in addition to the core persistence contracts.

The in-memory provider currently supports the complete Edge capability set. The SQL Server provider is currently **core-only**: it supports observation ingestion, production-context persistence, metric-input persistence, and FC-026 aggregate persistence, but not FC-027 historical-revision reconstruction or operational-metric projection/query persistence. Selecting SQL Server for the full Edge application therefore fails during persistence finalization instead of mixing SQL core state with in-memory FC-027 state.

## SQL Server Deployment Prerequisite

A production SQL Server database must be provisioned before starting FactoryConnect Edge for SQL-backed core persistence. Apply the provider-owned schema migrations to that database, then supply its connection string through normal .NET configuration or a secret store.

The runtime does not create production databases and FC-023 does not introduce an automatic migration framework. Database provisioning and credentials remain deployment/infrastructure responsibilities.

SQL-backed FC-027 operational-metric durability remains future provider work.

## Initial Deployment Scope

The first deployment scope targets industrial machine connectivity through Ethernet-capable controllers and retrofit I/O gateways, with MTConnect and Modbus TCP feeding the same canonical FactoryConnect model.

## Project Status

FactoryConnect has progressed through **FC-027 — Operational Metric Evaluation and Reporting Model**. The durable pipeline now spans acquisition, canonical signal processing, machine state/activity history, historically effective production and shift context, planned-production eligibility, durable metric-input facts, shift/production-day component aggregation, exact-version operational metric evaluation, durable metric projections, and provider-neutral reporting reads.

FC-027 includes built-in Availability, Utilization, Performance, Quality, and OEE definitions; exact-version dependency evaluation; coherent historical source revisions; durable replay-safe projections; lightweight period summaries; and exact-version recursive lineage detail.

HTTP reporting APIs, dashboards, cross-machine/site rollups, downtime reason workflows, manual backfill/re-evaluation, SQL-backed FC-027 projection persistence, alerting, and predictive metrics remain downstream work.

See `docs/features/FC-027-operational-metric-evaluation-reporting-model.md` for the FC-027 architecture and conformance model.

## License

To be defined.