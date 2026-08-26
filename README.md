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
Applications / Analytics / AI
```

## Current Capabilities

- Canonical machine and observation contracts
- Modbus TCP and MTConnect protocol adapters
- MTConnect discovery, current and sequence-aware sample acquisition
- Continuous Edge acquisition with transient retry and continuity recovery
- Durable observation ingestion with atomic cursor checkpointing
- Pluggable persistence-provider selection
- In-memory persistence provider
- SQL Server persistence provider with transactional commits, idempotent replay, exact stream identity, and same-stream concurrency protection
- Durable raw-to-canonical observation processing with independent checkpoints
- Durable machine state changes and activity-period projection
- Effective-dated production context with company, site, line, machine, order, operation, part, and operator dimensions
- Flexible recurring shift schedules with overnight shifts, calendar overrides, line precedence, and deterministic DST handling
- Context/shift interval allocation with deterministic lineage and duration conservation
- Planned-production windows, breaks, shutdowns, and replacement overrides
- Durable planned-production eligibility facts
- Durable duration and explicit quantity metric-input facts with replay-stable identity
- Independent activity and quantity processors with atomic output/checkpoint commits and restart/replay conformance
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
7. Metric inputs are durable evidence facts; final KPI percentages and downtime classification are downstream concerns.
8. Persistence providers are replaceable; available providers are not automatically active providers.
9. The Edge runtime must operate independently of the dashboard UI.
10. Hardware is replaceable through connector abstractions.
11. The software must be testable without physical factory hardware.
12. PulseStackAI is a separate platform and may consume FactoryConnect data for AI orchestration.

## Persistence

FactoryConnect selects exactly one observation-ingestion provider at the composition root.

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

## SQL Server Deployment Prerequisite

A production SQL Server database must be provisioned before starting FactoryConnect Edge. Apply the provider-owned `001_InitialObservationIngestion.sql` schema to that database, then supply its connection string through normal .NET configuration or a secret store.

The runtime does not create production databases and FC-023 does not introduce an automatic migration framework. Database provisioning and credentials remain deployment/infrastructure responsibilities.

## Initial Deployment Scope

The first deployment scope targets industrial machine connectivity through Ethernet-capable controllers and retrofit I/O gateways, with MTConnect and Modbus TCP feeding the same canonical FactoryConnect model.

## Project Status

FactoryConnect has progressed through **FC-025 — Durable Production Context and Metric Input Derivation**. The durable pipeline now spans acquisition, canonical signal processing, machine state/activity history, historically effective production and shift context, planned-production eligibility, and durable duration/quantity metric-input facts with independent processor checkpoints and restart/replay conformance.

Final KPI aggregation, OEE/utilization evaluation, downtime reason classification, reporting queries, and SQL persistence for FC-025 projection outputs remain downstream work.

See `docs/features/FC-025-durable-production-context-metric-input.md` for the FC-025 architecture and conformance model.

## License

To be defined.
